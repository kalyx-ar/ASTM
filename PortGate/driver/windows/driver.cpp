// Windows 10/11 UMDF 2 frontend. No code here opens a physical device: all payload goes through the PortGate arbiter.
#define WIN32_NO_STATUS
#define UMDF_USING_NTSTATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <wdf.h>
#include <ntddser.h>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include "worker.hpp"
#include "PortGateVComIoctl.h"

using portgate::VirtualComEvent;
using portgate::vcom::Worker;
namespace {
constexpr std::size_t kRxLimit=1024*1024;
struct RequestContext {
    ULONGLONG id=0, since=0, last_byte=0, total_timeout=0;
    std::size_t progress=0;
    std::size_t requested=0;
    ULONG interval=0;
    bool immediate=false,first_byte=false;
};
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(RequestContext, RequestData);
struct State {
    WDFDEVICE device=nullptr;
    WDFTIMER timer=nullptr;
    WDFQUEUE reads=nullptr, active_read=nullptr, writes=nullptr, waits=nullptr, state_waits=nullptr, flushes=nullptr;
    std::mutex mutex;
    std::shared_ptr<Worker> worker;
    std::deque<std::uint8_t> received;
    std::string name;
    std::wstring pdo;
    PORTGATE_VCOM_STATUS status{PORTGATE_VCOM_VERSION,0,0,{}};
    SERIAL_TIMEOUTS timeouts{};
    SERIAL_BAUD_RATE baud{9600};
    SERIAL_LINE_CONTROL line{STOP_BIT_1,NO_PARITY,8};
    SERIAL_HANDFLOW flow{};
    SERIAL_CHARS chars{0x1a,0,0,0,0x11,0x13};
    ULONG wait_mask=0, pending_events=0;
    ULONG serial_errors=0;
    ULONGLONG next_id=0;
    bool opening=false;
    bool deleting=false;
};
struct DeviceContext { State* state=nullptr; };
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DeviceContext, DeviceData);

ULONGLONG clock_ms() {
    return static_cast<ULONGLONG>(std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
}
void complete_queue(WDFQUEUE queue, NTSTATUS status) {
    WDFREQUEST request=nullptr;
    while(NT_SUCCESS(WdfIoQueueRetrieveNextRequest(queue,&request))) WdfRequestComplete(request,status);
}
NTSTATUS forward(WDFREQUEST request,WDFQUEUE queue) {
    const auto result=WdfRequestForwardToIoQueue(request,queue);
    if(!NT_SUCCESS(result)) WdfRequestComplete(request,result);
    return result;
}
template<class T> bool input(WDFREQUEST request,T& value) {
    void* p=nullptr;
    if(!NT_SUCCESS(WdfRequestRetrieveInputBuffer(request,sizeof(T),&p,nullptr))) return false;
    std::memcpy(&value,p,sizeof(T)); return true;
}
template<class T> void output(WDFREQUEST request,const T& value) {
    void* p=nullptr;
    const auto status=WdfRequestRetrieveOutputBuffer(request,sizeof(T),&p,nullptr);
    if(NT_SUCCESS(status)) { std::memcpy(p,&value,sizeof(T)); WdfRequestCompleteWithInformation(request,STATUS_SUCCESS,sizeof(T)); }
    else WdfRequestComplete(request,status);
}
void notify_waiters(State& s) {
    WDFREQUEST request=nullptr;
    if(s.pending_events&s.wait_mask) {
        if(NT_SUCCESS(WdfIoQueueRetrieveNextRequest(s.waits,&request))) {
            const auto events=s.pending_events&s.wait_mask; s.pending_events&=~events; output(request,events);
        }
    }
}
void notify_state(State& s) {
    WDFREQUEST request=nullptr;
    while(NT_SUCCESS(WdfIoQueueRetrieveNextRequest(s.state_waits,&request))) output(request,s.status);
}
// Must hold State::mutex. A separate one-request queue preserves read order while Windows owns cancellation.
void drain_reads(State& s) {
    for(;;) {
        WDFREQUEST request=nullptr;
        if(!NT_SUCCESS(WdfIoQueueRetrieveNextRequest(s.active_read,&request)) && !NT_SUCCESS(WdfIoQueueRetrieveNextRequest(s.reads,&request))) return;
        void* target=nullptr; std::size_t length=0;
        const auto result=WdfRequestRetrieveOutputBuffer(request,1,&target,&length);
        if(!NT_SUCCESS(result)) { WdfRequestComplete(request,result); continue; }
        auto* r=RequestData(request); const auto now=clock_ms();
        length=(std::min)(length,r->requested);
        const auto before=r->progress;
        while(r->progress<length && !s.received.empty()) {
            static_cast<std::uint8_t*>(target)[r->progress++]=s.received.front(); s.received.pop_front();
        }
        if(r->progress>before) r->last_byte=now;
        const bool interval=r->progress>0 && r->interval>0 && r->interval!=MAXDWORD && now-r->last_byte>=r->interval;
        const bool expired=r->total_timeout>0 && now-r->since>=r->total_timeout;
        if(r->progress==length || r->immediate || (r->first_byte && r->progress>0) || interval || expired || s.status.State==0) {
            WdfRequestCompleteWithInformation(request,s.status.State==0 && r->progress==0 ? STATUS_DEVICE_NOT_CONNECTED : STATUS_SUCCESS,r->progress);
            continue;
        }
        forward(request,s.active_read); return;
    }
}
void finish_write(State& s,const VirtualComEvent& event) {
    // Find/retrieve keeps cancellation under WDF ownership, including cancelled writes whose bytes already reached Link.
    WDFREQUEST previous=nullptr, found=nullptr;
    while(NT_SUCCESS(WdfIoQueueFindRequest(s.writes,previous,nullptr,nullptr,&found))) {
        if(previous) WdfObjectDereferenceActual(previous,nullptr,0,nullptr);
        previous=found;
        if(RequestData(found)->id==event.request) {
            WDFREQUEST request=nullptr;
            if(NT_SUCCESS(WdfIoQueueRetrieveFoundRequest(s.writes,found,&request)))
                WdfRequestCompleteWithInformation(request,event.detail.empty()?STATUS_SUCCESS:
                    event.detail=="PORTGATE-VCOM-WRITE-TIMEOUT"?STATUS_IO_TIMEOUT:STATUS_IO_DEVICE_ERROR,event.written);
            break;
        }
    }
    if(previous) WdfObjectDereferenceActual(previous,nullptr,0,nullptr);
}
bool on_event(State& s,const VirtualComEvent& event) {
    std::lock_guard<std::mutex> lock(s.mutex);
    bool healthy=true;
    switch(event.kind) {
        case VirtualComEvent::Kind::Opened:
            s.status.State=1; s.pending_events|=SERIAL_EV_RLSD|SERIAL_EV_DSR|SERIAL_EV_CTS; break;
        case VirtualComEvent::Kind::Received:
            if(s.received.size()+event.data.size()>kRxLimit) {
                s.status.State=0; s.pending_events|=SERIAL_EV_ERR|SERIAL_EV_RLSD;
                wcscpy_s(s.status.Reason,L"PORTGATE-VCOM-RX-OVERFLOW");
                healthy=false;
                // Preserve preceding bytes; discard this whole chunk explicitly and require reopening.
            } else {
                s.received.insert(s.received.end(),event.data.begin(),event.data.end());
                s.pending_events|=SERIAL_EV_RXCHAR;
                if(std::find(event.data.begin(),event.data.end(),s.chars.EventChar)!=event.data.end()) s.pending_events|=SERIAL_EV_RXFLAG;
            }
            break;
        case VirtualComEvent::Kind::Written:
            finish_write(s,event);
            { ULONG pending=0;WdfIoQueueGetState(s.writes,&pending,nullptr);if(!pending) {
                s.pending_events|=SERIAL_EV_TXEMPTY;
                complete_queue(s.flushes,event.detail.empty()?STATUS_SUCCESS:STATUS_IO_DEVICE_ERROR);
            } }
            break;
        case VirtualComEvent::Kind::Warning:
            s.status.State=2; s.status.WarningId=event.request;
            MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,event.detail.c_str(),-1,s.status.Reason,256);
            s.status.Reason[255]=0; notify_state(s); break;
        case VirtualComEvent::Kind::Disconnected:
            s.status.State=0; s.pending_events|=SERIAL_EV_RLSD|SERIAL_EV_DSR|SERIAL_EV_CTS;
            MultiByteToWideChar(CP_UTF8,0,event.detail.c_str(),-1,s.status.Reason,256); s.status.Reason[255]=0;
            complete_queue(s.writes,STATUS_DEVICE_NOT_CONNECTED); notify_state(s); break;
        case VirtualComEvent::Kind::RetirementCancelled:
            s.status.State=1; s.status.Reason[0]=0; notify_state(s); break;
        case VirtualComEvent::Kind::Discontinuity:
            s.serial_errors|=SERIAL_ERROR_OVERRUN; s.pending_events|=SERIAL_EV_ERR; break;
    }
    drain_reads(s); notify_waiters(s);
    if(event.kind==VirtualComEvent::Kind::Disconnected) complete_queue(s.waits,STATUS_DEVICE_NOT_CONNECTED);
    if(event.kind==VirtualComEvent::Kind::Disconnected) complete_queue(s.flushes,STATUS_DEVICE_NOT_CONNECTED);
    return healthy;
}
EVT_WDF_IO_QUEUE_IO_CANCELED_ON_QUEUE cancelled;
void cancelled(WDFQUEUE,WDFREQUEST request) { WdfRequestComplete(request,STATUS_CANCELLED); }
EVT_WDF_FILE_CLEANUP file_cleanup;
void file_cleanup(WDFFILEOBJECT file) {
    auto& s=*DeviceData(WdfFileObjectGetDevice(file))->state;
    std::shared_ptr<Worker> worker;
    { std::lock_guard<std::mutex> lock(s.mutex); worker.swap(s.worker); }
    if(worker) worker->close(); // No State lock while joining a worker that may still deliver its final callback.
    std::lock_guard<std::mutex> lock(s.mutex);
    s.status.State=0; s.received.clear(); s.opening=false;
    complete_queue(s.reads,STATUS_CANCELLED); complete_queue(s.active_read,STATUS_CANCELLED);
    complete_queue(s.writes,STATUS_CANCELLED); complete_queue(s.waits,STATUS_CANCELLED); complete_queue(s.state_waits,STATUS_CANCELLED);
    complete_queue(s.flushes,STATUS_CANCELLED);
}
EVT_WDF_DEVICE_FILE_CREATE file_create;
void file_create(WDFDEVICE device,WDFREQUEST request,WDFFILEOBJECT) {
    auto& s=*DeviceData(device)->state;
    std::shared_ptr<Worker> worker;
    { std::lock_guard<std::mutex> lock(s.mutex);
      if(s.opening || s.worker) { WdfRequestComplete(request,STATUS_ACCESS_DENIED); return; }
      s.opening=true; s.received.clear(); s.status={PORTGATE_VCOM_VERSION,0,0,{}}; s.wait_mask=0; s.pending_events=0; s.serial_errors=0;
      s.timeouts={}; s.baud={9600}; s.line={STOP_BIT_1,NO_PARITY,8}; s.flow={}; s.chars={0x1a,0,0,0,0x11,0x13};
      worker=std::make_shared<Worker>(s.name,[&s](const VirtualComEvent& event){return on_event(s,event);}); s.worker=worker;
    }
    if(worker->start()) WdfRequestComplete(request,STATUS_SUCCESS);
    else {
        worker->close();
        { std::lock_guard<std::mutex> lock(s.mutex); s.worker.reset(); s.opening=false; }
        WdfRequestComplete(request,STATUS_DEVICE_NOT_CONNECTED);
    }
}
EVT_WDF_IO_QUEUE_IO_READ read;
void read(WDFQUEUE queue,WDFREQUEST request,size_t length) {
    auto& s=*DeviceData(WdfIoQueueGetDevice(queue))->state; std::lock_guard<std::mutex> lock(s.mutex);
    if(!length) { WdfRequestCompleteWithInformation(request,STATUS_SUCCESS,0); return; }
    auto* r=RequestData(request); *r={}; r->since=clock_ms();r->requested=length;r->interval=s.timeouts.ReadIntervalTimeout;
    const auto multiplier=s.timeouts.ReadTotalTimeoutMultiplier;
    r->immediate=r->interval==MAXDWORD && multiplier==0 && s.timeouts.ReadTotalTimeoutConstant==0;
    r->first_byte=r->interval==MAXDWORD && (multiplier==0 || multiplier==MAXDWORD) && s.timeouts.ReadTotalTimeoutConstant>0;
    r->total_timeout=static_cast<ULONGLONG>(s.timeouts.ReadTotalTimeoutConstant)+(multiplier==MAXDWORD?0:static_cast<ULONGLONG>(multiplier)*length);
    if(NT_SUCCESS(forward(request,s.reads))) drain_reads(s);
}
EVT_WDF_IO_QUEUE_IO_WRITE write;
void write(WDFQUEUE queue,WDFREQUEST request,size_t length) {
    auto& s=*DeviceData(WdfIoQueueGetDevice(queue))->state;
    if(!length) { WdfRequestCompleteWithInformation(request,STATUS_SUCCESS,0); return; }
    if(length>1024*1024) { WdfRequestComplete(request,STATUS_INVALID_BUFFER_SIZE); return; }
    void* data=nullptr; const auto result=WdfRequestRetrieveInputBuffer(request,length,&data,nullptr);
    if(!NT_SUCCESS(result)) { WdfRequestComplete(request,result); return; }
    const auto* first=static_cast<const std::uint8_t*>(data); portgate::link::Bytes copy(first,first+length);
    std::shared_ptr<Worker> worker; ULONGLONG id=0;
    { std::lock_guard<std::mutex> lock(s.mutex);
      if(s.status.State==0 || !s.worker) { WdfRequestComplete(request,STATUS_DEVICE_NOT_CONNECTED); return; }
      worker=s.worker; auto* r=RequestData(request); *r={}; id=r->id=++s.next_id; r->since=clock_ms();r->requested=length;
      r->total_timeout=static_cast<ULONGLONG>(s.timeouts.WriteTotalTimeoutConstant)+static_cast<ULONGLONG>(s.timeouts.WriteTotalTimeoutMultiplier)*length;
      if(!NT_SUCCESS(forward(request,s.writes))) return;
    }
    if(!worker->write(id,copy)) on_event(s,{VirtualComEvent::Kind::Written,id,0,{},"PORTGATE-VCOM-WRITE-REFUSED"});
}
EVT_WDF_TIMER timer;
void timer(WDFTIMER timer_object) {
    auto& s=*DeviceData(WdfTimerGetParentObject(timer_object))->state;
    std::shared_ptr<Worker> timed_out;
    { std::lock_guard<std::mutex> lock(s.mutex); drain_reads(s);
      WDFREQUEST previous=nullptr,found=nullptr;
      while(NT_SUCCESS(WdfIoQueueFindRequest(s.writes,previous,nullptr,nullptr,&found))) {
          if(previous) WdfObjectDereferenceActual(previous,nullptr,0,nullptr);previous=found;
          const auto* r=RequestData(found);
          if(r->total_timeout && clock_ms()-r->since>=r->total_timeout) {timed_out=s.worker;break;}
      }
      if(previous) WdfObjectDereferenceActual(previous,nullptr,0,nullptr);
    }
    if(timed_out) { timed_out->fault("PORTGATE-VCOM-WRITE-TIMEOUT"); timed_out->close(); } // Never replay into another tenure.
    std::lock_guard<std::mutex> lock(s.mutex);
    if(!s.deleting) WdfTimerStart(timer_object,WDF_REL_TIMEOUT_IN_MS(10));
}

EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL control;
void control(WDFQUEUE queue,WDFREQUEST request,size_t,size_t,ULONG code) {
    auto& s=*DeviceData(WdfIoQueueGetDevice(queue))->state;
    if(code==IOCTL_PORTGATE_VCOM_COMPLETE_TASK) {
        ULONGLONG warning=0; if(!input(request,warning)) { WdfRequestComplete(request,STATUS_BUFFER_TOO_SMALL); return; }
        std::shared_ptr<Worker> worker;
        { std::lock_guard<std::mutex> lock(s.mutex); worker=s.worker; }
        WdfRequestComplete(request,worker && worker->complete_task(warning)?STATUS_SUCCESS:STATUS_INVALID_DEVICE_STATE); return;
    }
    std::lock_guard<std::mutex> lock(s.mutex);
    NTSTATUS result=STATUS_SUCCESS;
    switch(code) {
        case IOCTL_PORTGATE_VCOM_STATE: output(request,s.status); return;
        case IOCTL_PORTGATE_VCOM_WAIT_STATE: {
            ULONGLONG known=0;
            if(!input(request,known)) { result=STATUS_BUFFER_TOO_SMALL; break; }
            if(s.status.State==0 || known!=s.status.WarningId) { output(request,s.status); return; }
            forward(request,s.state_waits); return;
        }
        case IOCTL_SERIAL_GET_BAUD_RATE: output(request,s.baud); return;
        case IOCTL_SERIAL_SET_BAUD_RATE: {
            SERIAL_BAUD_RATE v{}; if(!input(request,v) || !v.BaudRate) result=STATUS_INVALID_PARAMETER;
            else s.baud=v; break;
        }
        case IOCTL_SERIAL_GET_LINE_CONTROL: output(request,s.line); return;
        case IOCTL_SERIAL_SET_LINE_CONTROL: {
            SERIAL_LINE_CONTROL v{};
            if(!input(request,v) || v.WordLength<5 || v.WordLength>8 || v.Parity>SPACE_PARITY || v.StopBits>STOP_BITS_2) result=STATUS_INVALID_PARAMETER;
            else s.line=v; break;
        }
        case IOCTL_SERIAL_GET_TIMEOUTS: output(request,s.timeouts); return;
        case IOCTL_SERIAL_SET_TIMEOUTS: {
            SERIAL_TIMEOUTS v{};
            if(!input(request,v) || (v.ReadIntervalTimeout==MAXDWORD && v.ReadTotalTimeoutMultiplier==MAXDWORD && v.ReadTotalTimeoutConstant==MAXDWORD)) result=STATUS_INVALID_PARAMETER;
            else s.timeouts=v; break;
        }
        case IOCTL_SERIAL_GET_HANDFLOW: output(request,s.flow); return;
        case IOCTL_SERIAL_SET_HANDFLOW: if(!input(request,s.flow)) result=STATUS_BUFFER_TOO_SMALL; break;
        case IOCTL_SERIAL_GET_CHARS: output(request,s.chars); return;
        case IOCTL_SERIAL_SET_CHARS: if(!input(request,s.chars)) result=STATUS_BUFFER_TOO_SMALL; break;
        case IOCTL_SERIAL_GET_WAIT_MASK: output(request,s.wait_mask); return;
        case IOCTL_SERIAL_SET_WAIT_MASK: {
            ULONG mask=0; if(!input(request,mask)) { result=STATUS_BUFFER_TOO_SMALL; break; }
            WDFREQUEST waiting=nullptr;
            while(NT_SUCCESS(WdfIoQueueRetrieveNextRequest(s.waits,&waiting))) output(waiting,ULONG{0});
            s.wait_mask=mask; s.pending_events&=mask; break;
        }
        case IOCTL_SERIAL_WAIT_ON_MASK: {
            ULONG count=0; WdfIoQueueGetState(s.waits,&count,nullptr);
            if(!s.wait_mask || count) { result=STATUS_INVALID_PARAMETER; break; }
            if(s.status.State==0 && !(s.pending_events&s.wait_mask)) { result=STATUS_DEVICE_NOT_CONNECTED; break; }
            if(NT_SUCCESS(forward(request,s.waits))) notify_waiters(s); return;
        }
        case IOCTL_SERIAL_GET_MODEMSTATUS: output(request,ULONG{s.status.State ? SERIAL_CTS_STATE|SERIAL_DSR_STATE|SERIAL_DCD_STATE : 0}); return;
        case IOCTL_SERIAL_GET_COMMSTATUS: {
            SERIAL_STATUS v{}; v.AmountInInQueue=static_cast<ULONG>(s.received.size()); v.Errors=s.serial_errors; s.serial_errors=0;
            WDFREQUEST previous=nullptr,found=nullptr; std::uint64_t bytes=0;
            while(NT_SUCCESS(WdfIoQueueFindRequest(s.writes,previous,nullptr,nullptr,&found))) {
                if(previous) WdfObjectDereferenceActual(previous,nullptr,0,nullptr);previous=found;
                bytes+=RequestData(found)->requested;
            }
            if(previous) WdfObjectDereferenceActual(previous,nullptr,0,nullptr);
            v.AmountInOutQueue=static_cast<ULONG>((std::min)(bytes,std::uint64_t{MAXDWORD}));
            output(request,v); return;
        }
        case IOCTL_SERIAL_GET_PROPERTIES: {
            SERIAL_COMMPROP v{}; v.PacketLength=sizeof(v); v.PacketVersion=2; v.ServiceMask=SERIAL_SP_SERIALCOMM;
            v.MaxTxQueue=v.MaxRxQueue=static_cast<ULONG>(kRxLimit); v.CurrentTxQueue=v.CurrentRxQueue=static_cast<ULONG>(kRxLimit);
            v.MaxBaud=SERIAL_BAUD_USER; v.ProvSubType=SERIAL_SP_RS232;
            v.ProvCapabilities=SERIAL_PCF_TOTALTIMEOUTS|SERIAL_PCF_INTTIMEOUTS;
            v.SettableParams=SERIAL_SP_BAUD|SERIAL_SP_DATABITS|SERIAL_SP_PARITY|SERIAL_SP_STOPBITS;
            v.SettableBaud=SERIAL_BAUD_USER; v.SettableData=SERIAL_DATABITS_5|SERIAL_DATABITS_6|SERIAL_DATABITS_7|SERIAL_DATABITS_8;
            v.SettableStopParity=SERIAL_STOPBITS_10|SERIAL_STOPBITS_15|SERIAL_STOPBITS_20|SERIAL_PARITY_NONE|SERIAL_PARITY_ODD|SERIAL_PARITY_EVEN|SERIAL_PARITY_MARK|SERIAL_PARITY_SPACE;
            output(request,v); return;
        }
        case IOCTL_SERIAL_SET_QUEUE_SIZE: {
            SERIAL_QUEUE_SIZE v{};
            if(!input(request,v) || v.InSize>kRxLimit || v.OutSize>kRxLimit) result=STATUS_INVALID_PARAMETER; break;
        }
        case IOCTL_SERIAL_PURGE: {
            ULONG mask=0; if(!input(request,mask) || mask&~(SERIAL_PURGE_RXABORT|SERIAL_PURGE_RXCLEAR|SERIAL_PURGE_TXABORT|SERIAL_PURGE_TXCLEAR)) { result=STATUS_INVALID_PARAMETER; break; }
            if(mask&SERIAL_PURGE_RXCLEAR) s.received.clear();
            if(mask&SERIAL_PURGE_RXABORT) { complete_queue(s.reads,STATUS_CANCELLED); complete_queue(s.active_read,STATUS_CANCELLED); }
            if(mask&(SERIAL_PURGE_TXABORT|SERIAL_PURGE_TXCLEAR)) complete_queue(s.writes,STATUS_CANCELLED);
            break;
        }
        case IOCTL_SERIAL_SET_DTR: s.flow.ControlHandShake=(s.flow.ControlHandShake&~SERIAL_DTR_MASK)|SERIAL_DTR_CONTROL; break;
        case IOCTL_SERIAL_CLR_DTR: s.flow.ControlHandShake&=~SERIAL_DTR_MASK; break;
        case IOCTL_SERIAL_SET_RTS: s.flow.FlowReplace=(s.flow.FlowReplace&~SERIAL_RTS_MASK)|SERIAL_RTS_CONTROL; break;
        case IOCTL_SERIAL_CLR_RTS: s.flow.FlowReplace&=~SERIAL_RTS_MASK; break;
        case IOCTL_SERIAL_GET_DTRRTS: output(request,ULONG{((s.flow.ControlHandShake&SERIAL_DTR_MASK)?SERIAL_DTR_STATE:0)|((s.flow.FlowReplace&SERIAL_RTS_MASK)?SERIAL_RTS_STATE:0)}); return;
        default: result=STATUS_NOT_SUPPORTED; break; // Never pretend that an unsupported hardware operation succeeded.
    }
    WdfRequestComplete(request,result);
}
EVT_WDF_IO_QUEUE_IO_DEFAULT default_io;
void default_io(WDFQUEUE queue,WDFREQUEST request) {
    auto& s=*DeviceData(WdfIoQueueGetDevice(queue))->state;std::lock_guard<std::mutex> lock(s.mutex);
    WDF_REQUEST_PARAMETERS parameters;WDF_REQUEST_PARAMETERS_INIT(&parameters);WdfRequestGetParameters(request,&parameters);
    if(parameters.Type!=WdfRequestTypeFlushBuffers) {WdfRequestComplete(request,STATUS_NOT_SUPPORTED);return;}
    if(!s.status.State) {WdfRequestComplete(request,STATUS_DEVICE_NOT_CONNECTED);return;}
    ULONG pending=0;WdfIoQueueGetState(s.writes,&pending,nullptr);
    if(pending) forward(request,s.flushes);else WdfRequestComplete(request,STATUS_SUCCESS);
}
EVT_WDF_OBJECT_CONTEXT_CLEANUP device_cleanup;
void device_cleanup(WDFOBJECT device) {
    auto* s=DeviceData(device)->state; if(!s) return;
    { std::lock_guard<std::mutex> lock(s->mutex); s->deleting=true; }
    if(s->timer) WdfTimerStop(s->timer,TRUE);
    std::shared_ptr<Worker> worker;
    { std::lock_guard<std::mutex> lock(s->mutex); worker.swap(s->worker); }
    if(worker) worker->close();
    if(!s->pdo.empty()) {
        DECLARE_CONST_UNICODE_STRING(subkey,L"SERIALCOMM");
        WDFKEY key=nullptr;
        if(NT_SUCCESS(WdfDeviceOpenDevicemapKey(s->device,&subkey,KEY_SET_VALUE,WDF_NO_OBJECT_ATTRIBUTES,&key))) {
            UNICODE_STRING name{}; name.Buffer=s->pdo.data(); name.Length=static_cast<USHORT>(s->pdo.size()*sizeof(wchar_t)); name.MaximumLength=name.Length;
            WdfRegistryRemoveValue(key,&name); WdfRegistryClose(key); // Remove only this device's value.
        }
    }
    delete s; DeviceData(device)->state=nullptr;
}
EVT_WDF_DRIVER_DEVICE_ADD add;
NTSTATUS add(WDFDRIVER,PWDFDEVICE_INIT init) {
    WdfDeviceInitSetIoType(init,WdfDeviceIoBuffered);
    WDF_FILEOBJECT_CONFIG files; WDF_FILEOBJECT_CONFIG_INIT(&files,file_create,WDF_NO_EVENT_CALLBACK,file_cleanup);
    WdfDeviceInitSetFileObjectConfig(init,&files,WDF_NO_OBJECT_ATTRIBUTES);
    WDF_OBJECT_ATTRIBUTES requests; WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&requests,RequestContext);
    WdfDeviceInitSetRequestAttributes(init,&requests);
    WDF_OBJECT_ATTRIBUTES attributes; WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes,DeviceContext);
    attributes.EvtCleanupCallback=device_cleanup; attributes.ExecutionLevel=WdfExecutionLevelPassive; attributes.SynchronizationScope=WdfSynchronizationScopeNone;
    WDFDEVICE device=nullptr; auto status=WdfDeviceCreate(&init,&attributes,&device); if(!NT_SUCCESS(status)) return status;
    auto* s=new(std::nothrow) State; if(!s) return STATUS_INSUFFICIENT_RESOURCES; DeviceData(device)->state=s; s->device=device;
    DECLARE_CONST_UNICODE_STRING(value,L"PortName"); WDFKEY key=nullptr;
    status=WdfDeviceOpenRegistryKey(device,PLUGPLAY_REGKEY_DEVICE,KEY_QUERY_VALUE,WDF_NO_OBJECT_ATTRIBUTES,&key);
    if(!NT_SUCCESS(status)) return status;
    wchar_t com[16]{}; UNICODE_STRING name{}; name.Buffer=com; name.MaximumLength=sizeof(com);
    status=WdfRegistryQueryUnicodeString(key,&value,nullptr,&name); WdfRegistryClose(key); if(!NT_SUCCESS(status)) return status;
    for(USHORT i=0;i<name.Length/sizeof(wchar_t);++i) { if(com[i]>127) return STATUS_INVALID_PARAMETER; s->name.push_back(static_cast<char>(com[i])); }
    if(s->name.size()<4 || s->name.size()>8 || s->name.substr(0,3)!="COM" || s->name[3]=='0' || s->name.find_first_not_of("0123456789",3)!=std::string::npos) return STATUS_INVALID_PARAMETER;
    const auto symbolic=std::wstring(L"\\DosDevices\\")+std::wstring(com,name.Length/sizeof(wchar_t));
    UNICODE_STRING link{}; link.Buffer=const_cast<wchar_t*>(symbolic.c_str()); link.Length=static_cast<USHORT>(symbolic.size()*sizeof(wchar_t)); link.MaximumLength=link.Length;
    status=WdfDeviceCreateSymbolicLink(device,&link); if(!NT_SUCCESS(status)) return status;
    status=WdfDeviceCreateDeviceInterface(device,&GUID_DEVINTERFACE_COMPORT,nullptr); if(!NT_SUCCESS(status)) return status;
    WDFMEMORY memory=nullptr;
    status=WdfDeviceAllocAndQueryProperty(device,DevicePropertyPhysicalDeviceObjectName,NonPagedPool,WDF_NO_OBJECT_ATTRIBUTES,&memory);
    if(!NT_SUCCESS(status)) return status;
    const auto* pdo=static_cast<const wchar_t*>(WdfMemoryGetBuffer(memory,nullptr)); s->pdo=pdo; WdfObjectDelete(memory);
    DECLARE_CONST_UNICODE_STRING(subkey,L"SERIALCOMM");
    status=WdfDeviceOpenDevicemapKey(device,&subkey,KEY_SET_VALUE,WDF_NO_OBJECT_ATTRIBUTES,&key); if(!NT_SUCCESS(status)) return status;
    UNICODE_STRING pdo_name{}; pdo_name.Buffer=s->pdo.data(); pdo_name.Length=static_cast<USHORT>(s->pdo.size()*sizeof(wchar_t)); pdo_name.MaximumLength=pdo_name.Length;
    status=WdfRegistryAssignUnicodeString(key,&pdo_name,&name); WdfRegistryClose(key); if(!NT_SUCCESS(status)) return status;
    for(auto target:{&s->reads,&s->active_read,&s->writes,&s->waits,&s->state_waits,&s->flushes}) {
        WDF_IO_QUEUE_CONFIG config; WDF_IO_QUEUE_CONFIG_INIT(&config,WdfIoQueueDispatchManual); config.PowerManaged=WdfFalse; config.EvtIoCanceledOnQueue=cancelled;
        status=WdfIoQueueCreate(device,&config,WDF_NO_OBJECT_ATTRIBUTES,target); if(!NT_SUCCESS(status)) return status;
    }
    WDF_IO_QUEUE_CONFIG config; WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&config,WdfIoQueueDispatchParallel);
    config.PowerManaged=WdfFalse; config.EvtIoRead=read; config.EvtIoWrite=write; config.EvtIoDeviceControl=control;
    config.EvtIoDefault=default_io;
    status=WdfIoQueueCreate(device,&config,WDF_NO_OBJECT_ATTRIBUTES,nullptr); if(!NT_SUCCESS(status)) return status;
    // Passive-level WDF timers are one-shot; restart explicitly after each callback.
    WDF_TIMER_CONFIG tick; WDF_TIMER_CONFIG_INIT(&tick,timer); tick.AutomaticSerialization=FALSE;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes); attributes.ParentObject=device; attributes.ExecutionLevel=WdfExecutionLevelPassive;
    status=WdfTimerCreate(&tick,&attributes,&s->timer); if(NT_SUCCESS(status)) WdfTimerStart(s->timer,WDF_REL_TIMEOUT_IN_MS(10));
    return status;
}
}
extern "C" DRIVER_INITIALIZE DriverEntry;
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT object,PUNICODE_STRING registry) {
    WDF_DRIVER_CONFIG config; WDF_DRIVER_CONFIG_INIT(&config,add);
    return WdfDriverCreate(object,registry,WDF_NO_OBJECT_ATTRIBUTES,&config,WDF_NO_HANDLE);
}
