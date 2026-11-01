#include <iostream>
#include "portgate/arbiter.hpp"
#include "portgate/config.hpp"
#include "portgate/virtual_com_protocol.hpp"
using namespace portgate;
using cbor::Value;
namespace {
link::Unit control(link::Type type,std::map<std::uint64_t,Value> fields) { return link::control(type,1,Value::map(std::move(fields))); }
link::Unit grant(std::uint64_t id,std::uint32_t generation=3) {
    return control(link::Type::Granted,{{0,Value::uint(id)},{1,Value::uint(generation)},{2,Value::bytes(std::vector<std::uint8_t>(16,4))},
        {3,Value::uint(3000)},{4,Value::uint(0)},{5,Value::boolean(false)},{6,Value::uint(0)},{7,Value::uint(0)},{8,Value::uint(0)}});
}
void open(VirtualComProtocol& p,std::size_t max_write=16384) {
    p.begin(); p.take_output();
    p.accept(link::control(link::Type::Hello,0,Value::map({{0,Value::uint(1)},{3,Value::bytes(std::vector<std::uint8_t>(16,7))},{5,Value::uint(65536)}})));
    p.accept(link::control(link::Type::Capabilities,0,Value::map({{0,Value::array({Value::map({{5,Value::uint(max_write)},{8,Value::text("COM15")}})})}})));
    p.take_output(); p.accept(grant(1)); p.take_output(); p.accept(grant(2)); p.take_events();
}
}
int main() {
    int checks=0,failures=0;
    auto check=[&](bool ok,int line){++checks;if(!ok){++failures;std::cerr<<"virtual_com_tests.cpp:"<<line<<" failed\n";}};
#define CHECK_VCOM(x) check((x),__LINE__)
    {
        Arbiter a(1000,8,{{"legacy",10},{"observer",20},{"other",0}},19);
        std::vector<ArbiterEvent> events;
        a.acquire(1,"legacy",1,0,0,events); const auto first=events.back(); CHECK_VCOM(a.confirm(1,first.generation,first.transfer_id)); events.clear();
        a.acquire(3,"other",3,0,1,events); a.acquire(2,"observer",2,0,1,events); events.clear();
        CHECK_VCOM(!a.transfer(1,first.generation+1,2,10,events));
        CHECK_VCOM(a.transfer(1,first.generation,2,10,events));
        CHECK_VCOM(a.owner()==2 && a.queued()==1 && !a.may_write(1,first.generation));
        CHECK_VCOM(events.size()==2 && events[0].code=="PORTGATE-SOFT-TRANSFER" && events[1].connection==2);
        CHECK_VCOM(a.confirm(2,events[1].generation,events[1].transfer_id));
        a.release(2,events[1].generation,11,events); CHECK_VCOM(a.owner()==3);
    }
    {
        VirtualComProtocol p("COM15"); open(p,2); CHECK_VCOM(p.active());
        CHECK_VCOM(p.write(42,{1,2,3})); auto output=p.take_output(); CHECK_VCOM(output.size()==2);
        p.accept(control(link::Type::WriteStatus,{{0,Value::uint(1)},{1,Value::uint(2)},{2,Value::uint(2)},{3,Value::uint(0)}}));
        p.accept(control(link::Type::WriteStatus,{{0,Value::uint(2)},{1,Value::uint(1)},{2,Value::uint(1)},{3,Value::uint(0)}}));
        auto events=p.take_events(); CHECK_VCOM(events.size()==1 && events[0].written==3 && events[0].detail.empty());
        link::SerialData rx; rx.generation=3; rx.data={5,6,4,0,255};
        p.accept(link::serial_data(1,rx)); events=p.take_events();
        CHECK_VCOM(events.size()==1 && events[0].data==rx.data && p.take_output().empty()); // No interpretation of ENQ/ACK/EOT.
        p.accept(control(link::Type::PauseVirtualCom,{{0,Value::uint(71)},{1,Value::uint(2)}})); CHECK_VCOM(p.active());
        p.accept(control(link::Type::PauseVirtualCom,{{0,Value::uint(71)},{1,Value::uint(3)}}));
        events=p.take_events(); CHECK_VCOM(events.size()==1 && events[0].kind==VirtualComEvent::Kind::Disconnected);
        CHECK_VCOM(!p.active() && !p.disconnected() && !p.write(43,{9}));
        output=p.take_output(); CHECK_VCOM(output.size()==1 && output[0].type==link::Type::VirtualComPaused);
        p.accept(control(link::Type::ResumeVirtualCom,{{0,Value::uint(72)},{1,Value::uint(3)}})); CHECK_VCOM(!p.active());
        p.accept(control(link::Type::ResumeVirtualCom,{{0,Value::uint(71)},{1,Value::uint(3)}})); CHECK_VCOM(p.active());
        p.take_events();
        p.accept(control(link::Type::PauseVirtualCom,{{0,Value::uint(73)},{1,Value::uint(3)}})); p.take_output(); p.take_events();
        p.accept(control(link::Type::Revoked,{{1,Value::uint(3)},{2,Value::text("PORTGATE-SOFT-TRANSFER")}}));
        p.accept(control(link::Type::PortState,{{0,Value::boolean(true)},{1,Value::text("observer")}})); CHECK_VCOM(p.take_output().empty());
        p.renew(); output=p.take_output(); CHECK_VCOM(output.size()==1 && output[0].type==link::Type::Heartbeat);
        p.accept(control(link::Type::PortState,{{0,Value::boolean(true)}})); output=p.take_output();
        CHECK_VCOM(output.size()==1 && output[0].type==link::Type::Acquire);
        p.accept(grant(1,5)); p.take_output(); p.accept(grant(2,5));
        CHECK_VCOM(p.active() && !p.disconnected() && p.write(44,{0,255})); output=p.take_output();
        CHECK_VCOM(output.size()==1 && link::parse_serial_data(output[0])->generation==5);
        p.close(); CHECK_VCOM(p.disconnected() && !p.write(45,{1}));
    }
    {
        VirtualComProtocol p("COM15"); open(p); CHECK_VCOM(p.write(11,{7,8})); p.take_output();
        p.accept(control(link::Type::WriteStatus,{{0,Value::uint(1)},{1,Value::uint(2)},{2,Value::uint(1)},{3,Value::uint(1)}})); CHECK_VCOM(p.disconnected());
        VirtualComProtocol gaps("COM15"); open(gaps); link::SerialData rx; rx.generation=3;rx.seq=1;rx.data={4};
        gaps.accept(link::serial_data(1,rx)); CHECK_VCOM(gaps.disconnected());
    }
    {
        AgentConfig c; c.agent_id="vcom"; c.ports.emplace_back(); auto& p=c.ports[0];
        p.port_id="p";p.device.path="COM1";p.virtual_com="COM15";p.controllers={{"vcom:COM15",10}};
        std::string error; auto decoded=decode_config(encode_config(c),error);
        CHECK_VCOM(decoded && decoded->ports[0].virtual_com_guard_ms==5000);
        p.virtual_com_guard_ms=0;error.clear(); decoded=decode_config(encode_config(c),error);CHECK_VCOM(decoded && !decoded->ports[0].virtual_com_guard_ms);
        p.virtual_com_guard_ms=600001;error.clear();CHECK_VCOM(!decode_config(encode_config(c),error));
        p.virtual_com_guard_ms=5000;p.virtual_com="COM1";error.clear();CHECK_VCOM(!decode_config(encode_config(c),error));
        p.virtual_com="COM0";error.clear();CHECK_VCOM(!decode_config(encode_config(c),error));
        p.virtual_com="COM15";p.controllers={{"someone",10}};error.clear();CHECK_VCOM(!decode_config(encode_config(c),error));
    }
    std::cout<<checks<<" virtual COM checks, "<<failures<<" failures\n"; return failures?1:0;
}