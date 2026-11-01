#include <algorithm>
#include "portgate/virtual_com_protocol.hpp"
namespace portgate {
namespace {
using cbor::Value;
std::uint64_t number(const Value& m,std::uint64_t k) { const auto* v=m.get(k); return v && v->as_uint()?*v->as_uint():0; }
std::string text(const Value& m,std::uint64_t k) { const auto* v=m.get(k); return v && v->as_text()?*v->as_text():std::string(); }
bool yes(const Value& m,std::uint64_t k) { const auto* v=m.get(k); return v && v->as_bool() && *v->as_bool(); }
}
VirtualComProtocol::VirtualComProtocol(std::string name):name_(std::move(name)) {}
void VirtualComProtocol::send(link::Type type,const Value& map) { output_.push_back(link::control(type,port_,map)); }
void VirtualComProtocol::begin() {
    output_.push_back(link::control(link::Type::Hello,0,Value::map({{0,Value::uint(1)},{1,Value::text("controller")},{2,Value::text("vcom:"+name_)}})));
}
void VirtualComProtocol::acquire() {
    acquiring_=true;
    send(link::Type::Acquire,Value::map({{0,Value::uint(1)},{2,Value::uint(3000)}}));
}
void VirtualComProtocol::accept(const link::Unit& unit) {
    if(disconnected_ || unit.type==link::Type::ObservedData) return;
    if(unit.type==link::Type::SerialData) {
        const auto data=link::parse_serial_data(unit);
        if(!generation_ || unit.channel!=port_ || !data || data->generation!=generation_) { fault("PORTGATE-VCOM-STALE-DATA"); return; }
        if(suspended_) return;
        if(rx_started_ && (data->offset!=rx_offset_ || data->seq!=rx_sequence_)) { fault("PORTGATE-VCOM-DISCONTINUITY"); return; }
        rx_started_=true; rx_offset_=data->offset+data->data.size(); rx_sequence_=data->seq+1;
        events_.push_back({VirtualComEvent::Kind::Received,0,0,data->data,{}}); return;
    }
    const auto m=link::parse_map(unit);
    if(!m) { fault("PORTGATE-VCOM-BAD-CONTROL"); return; }
    if(unit.type==link::Type::Hello) {
        const auto* epoch=m->get(3);
        if(hello_ || number(*m,0)!=1 || !epoch || !epoch->as_bytes() || epoch->as_bytes()->size()!=16 || number(*m,5)<=20) {
            fault("PORTGATE-VCOM-BAD-HELLO"); return;
        }
        hello_=true; max_payload_=(std::min)(std::size_t{65536},static_cast<std::size_t>(number(*m,5))); return;
    }
    if(unit.type==link::Type::Capabilities && !port_) {
        if(!hello_) { fault("PORTGATE-VCOM-HELLO-REQUIRED"); return; }
        const auto* field=m->get(0); const auto* ports=field?field->as_array():nullptr;
        if(ports) for(std::size_t i=0;i<ports->size();++i) if(text((*ports)[i],8)==name_) {
            port_=static_cast<std::uint16_t>(i+1); max_write_=static_cast<std::size_t>(number((*ports)[i],5));
            if(!max_write_) { fault("PORTGATE-VCOM-WRITE-LIMIT"); return; }
            max_write_=(std::min)(max_write_,max_payload_-20); acquire(); return;
        }
        fault("PORTGATE-VCOM-NOT-CONFIGURED"); return;
    }
    if(unit.channel!=port_) return;
    switch(unit.type) {
        case link::Type::Granted:
            if(number(*m,0)==1 && acquiring_) {
                acquiring_=false; generation_=static_cast<std::uint32_t>(number(*m,1)); lease_ms_=static_cast<std::uint32_t>(number(*m,3));
                rx_offset_=number(*m,7); rx_sequence_=number(*m,8); rx_started_=true; offset_=0;
                const auto* id=m->get(2);
                if(!generation_ || lease_ms_<1000 || !id || !id->as_bytes() || id->as_bytes()->size()!=16 || yes(*m,5) || number(*m,4)) {
                    fault("PORTGATE-VCOM-TRANSFER-UNCERTAIN"); return;
                }
                send(link::Type::ConfirmTransfer,Value::map({{1,Value::uint(generation_)},{2,*id}}));
                send(link::Type::Renew,Value::map({{0,Value::uint(2)},{1,Value::uint(generation_)}}));
            } else if(number(*m,1)==generation_ && number(*m,0)==2 && !confirmed_) {
                if(yes(*m,5)) { fault("PORTGATE-VCOM-TRANSFER-UNCERTAIN"); return; }
                if(number(*m,6)) events_.push_back({VirtualComEvent::Kind::Discontinuity,0,0,{},"PORTGATE-VCOM-UNOWNED-RX"});
                confirmed_=active_=true; suspended_=false;
                events_.push_back({VirtualComEvent::Kind::Opened,generation_,0,{}, {}});
            }
            break;
        case link::Type::WriteStatus: {
            const auto it=chunks_.find(number(*m,0)); if(it==chunks_.end()) break;
            const auto chunk=it->second; chunks_.erase(it); pending_bytes_-=chunk.size;
            auto& job=writes_.at(chunk.request); const auto written=static_cast<std::size_t>(number(*m,2));
            if(written>chunk.size || number(*m,3)!=0 || written!=chunk.size || number(*m,1)!=chunk.size) {
                if(written<=chunk.size && number(*m,3)==1) job.written+=written;
                fault("PORTGATE-VCOM-WRITE-UNCERTAIN"); return;
            }
            job.written+=written;
            if(--job.remaining==0) { events_.push_back({VirtualComEvent::Kind::Written,chunk.request,job.written,{}, {}}); writes_.erase(chunk.request); }
            break;
        }
        case link::Type::PauseVirtualCom:
            if(active_ && number(*m,1)==generation_) {
                pause_request_=number(*m,0); active_=false; suspended_=true;
                interrupt_writes("PORTGATE-SOFT-TRANSFER");
                events_.push_back({VirtualComEvent::Kind::Disconnected,0,0,{},"PORTGATE-SOFT-TRANSFER"});
                // Worker applies the disconnect in WDF before sending this internal acknowledgment. No legacy ACK.
                send(link::Type::VirtualComPaused,Value::map({{0,Value::uint(pause_request_)},{1,Value::uint(generation_)},{2,Value::boolean(true)}}));
            }
            break;
        case link::Type::ResumeVirtualCom:
            if(suspended_ && number(*m,0)==pause_request_ && number(*m,1)==generation_) {
                suspended_=false; active_=true;
                events_.push_back({VirtualComEvent::Kind::Opened,generation_,0,{}, {}});
            }
            break;
        case link::Type::Revoked:
            if(number(*m,1)==generation_) {
                if(suspended_ && text(*m,2)=="PORTGATE-SOFT-TRANSFER") { generation_=0; confirmed_=false; }
                else fault(text(*m,2));
            }
            break;
        case link::Type::PortState:
            if(suspended_ && !generation_ && !acquiring_ && yes(*m,0) && !m->get(1) && !yes(*m,4) && !yes(*m,5) && !yes(*m,7)) acquire();
            break;
        case link::Type::Denied: fault(text(*m,1)); break;
        case link::Type::PortEvent: fault(text(*m,1)); break;
        case link::Type::Error: fault(text(*m,0)); break;
        default: break;
    }
}
bool VirtualComProtocol::write(std::uint64_t request,const link::Bytes& bytes) {
    if(!active_ || disconnected_ || writes_.count(request) || bytes.empty() || bytes.size()>1024*1024 ||
       writes_.size()>=64 || pending_bytes_+bytes.size()>1024*1024 || chunks_.size()+(bytes.size()+max_write_-1)/max_write_>256) return false;
    auto& job=writes_[request];
    for(std::size_t at=0;at<bytes.size();at+=max_write_) {
        const auto n=(std::min)(max_write_,bytes.size()-at); const auto seq=++sequence_;
        link::SerialData data; data.seq=seq; data.offset=offset_; data.generation=generation_;
        data.data.assign(bytes.begin()+static_cast<std::ptrdiff_t>(at),bytes.begin()+static_cast<std::ptrdiff_t>(at+n)); offset_+=n;
        chunks_.emplace(seq,Chunk{request,n}); ++job.remaining; pending_bytes_+=n; output_.push_back(link::serial_data(port_,data));
    }
    return true;
}
void VirtualComProtocol::interrupt_writes(const std::string& reason) {
    for(const auto& [id,job]:writes_) events_.push_back({VirtualComEvent::Kind::Written,id,job.written,{},reason});
    writes_.clear(); chunks_.clear(); pending_bytes_=0;
    output_.erase(std::remove_if(output_.begin(),output_.end(),[](const link::Unit& u){return u.type==link::Type::SerialData;}),output_.end());
}
void VirtualComProtocol::renew() {
    if(disconnected_) return;
    if(active_) send(link::Type::Renew,Value::map({{0,Value::uint(3)},{1,Value::uint(generation_)}}));
    else send(link::Type::Heartbeat,Value::map({}));
}
void VirtualComProtocol::close() {
    if(disconnected_) return;
    if(generation_) send(link::Type::Release,Value::map({{1,Value::uint(generation_)}}));
    fault("PORTGATE-VCOM-CLOSED");
}
void VirtualComProtocol::fault(const std::string& reason) {
    if(disconnected_) return;
    active_=false; disconnected_=true; interrupt_writes(reason);
    events_.push_back({VirtualComEvent::Kind::Disconnected,0,0,{},reason});
}
std::vector<link::Unit> VirtualComProtocol::take_output() { std::vector<link::Unit> result; result.swap(output_); return result; }
std::vector<VirtualComEvent> VirtualComProtocol::take_events() { std::vector<VirtualComEvent> result; result.swap(events_); return result; }
}
