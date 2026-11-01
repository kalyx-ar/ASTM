#include <iostream>
#include "portgate/arbiter.hpp"
#include "portgate/config.hpp"
#include "portgate/lis01_results_boundary.hpp"
#include "portgate/virtual_com_protocol.hpp"
using namespace portgate;
using cbor::Value;
namespace {
link::Unit control(link::Type type,std::map<std::uint64_t,Value> fields) { return link::control(type,1,Value::map(std::move(fields))); }
void open(VirtualComProtocol& p,std::size_t max_write=16384) {
    p.begin(); p.take_output();
    p.accept(link::control(link::Type::Hello,0,Value::map({{0,Value::uint(1)},{3,Value::bytes(std::vector<std::uint8_t>(16,7))},{5,Value::uint(65536)}})));
    p.accept(link::control(link::Type::Capabilities,0,Value::map({{0,Value::array({Value::map({{5,Value::uint(max_write)},{8,Value::text("COM15")}})})}})));
    p.take_output();
    const auto grant=[](std::uint64_t id){return control(link::Type::Granted,{{0,Value::uint(id)},{1,Value::uint(3)},{2,Value::bytes(std::vector<std::uint8_t>(16,4))},
        {3,Value::uint(3000)},{4,Value::uint(0)},{5,Value::boolean(false)},{6,Value::uint(0)},{7,Value::uint(0)},{8,Value::uint(0)},{9,Value::uint(0)}});};
    p.accept(grant(1)); p.take_output(); p.accept(grant(2)); p.take_events();
}
link::Bytes frame(const std::string& text,bool final=true,int number=1) {
    link::Bytes b{2,static_cast<std::uint8_t>('0'+number)}; b.insert(b.end(),text.begin(),text.end()); b.push_back(final?3:0x17);
    unsigned sum=0; for(std::size_t i=1;i<b.size();++i) sum+=b[i]; const char* hex="0123456789ABCDEF";
    b.push_back(static_cast<std::uint8_t>(hex[(sum>>4)&15])); b.push_back(static_cast<std::uint8_t>(hex[sum&15])); b.push_back(13); b.push_back(10); return b;
}
void batch(Lis01ResultsBoundary& b,const link::Bytes& f,bool ack=true) {
    b.observe(true,{5}); b.observe(false,{6});
    for(auto byte:f) b.observe(true,{byte});
    if(ack) b.observe(false,{6}); b.observe(true,{4});
}
}
int main() {
    int checks=0,failures=0;
    auto check=[&](bool ok,int line){++checks;if(!ok){++failures;std::cerr<<"virtual_com_tests.cpp:"<<line<<" failed\n";}};
#define CHECK_VCOM(x) check((x),__LINE__)
    {
        Arbiter a(1000,8,{{"legacy",0},{"edc",1}},19,2000); std::vector<ArbiterEvent> events;
        a.acquire(1,"legacy",1,0,0,events); const auto first=events.back(); CHECK_VCOM(a.confirm(1,first.generation,first.transfer_id)); events.clear();
        a.acquire(2,"edc",2,0,1,events); CHECK_VCOM(a.queued()==1); events.clear();
        a.prepare_withdrawal(events); CHECK_VCOM(a.withdrawing() && a.owner()==1 && a.may_write(1,first.generation));
        CHECK_VCOM(a.queued()==0 && events.size()==1 && events[0].kind==ArbiterEvent::Kind::Denied); events.clear();
        a.renew(1,3,first.generation,500,events); CHECK_VCOM(a.owner()==1); events.clear();
        a.tick(1001,events); CHECK_VCOM(a.owner()==1); // Not the former expiry; renewal retained the real lease.
        a.acquire(2,"edc",4,0,1002,events); CHECK_VCOM(events.back().code=="PORTGATE-WITHDRAWING"); events.clear();
        a.release(1,first.generation,1003,events); CHECK_VCOM(!a.owner() && a.withdrawing());
        a.reopen(1004,events); a.acquire(2,"edc",5,0,1005,events); CHECK_VCOM(a.owner()==2 && !a.withdrawing());
        CHECK_VCOM(!a.may_write(1,first.generation));
    }
    {
        VirtualComProtocol p("COM15"); open(p,2); CHECK_VCOM(p.active());
        CHECK_VCOM(p.write(42,{1,2,3})); const auto tx=p.take_output(); CHECK_VCOM(tx.size()==2);
        p.accept(control(link::Type::RetirementNotice,{{0,Value::uint(3)},{1,Value::text("Dispositivo próximo a apagarse")},{2,Value::boolean(true)}}));
        CHECK_VCOM(p.active() && !p.disconnected() && p.take_output().empty());
        const auto warning=p.warning(); CHECK_VCOM(warning!=0 && !p.complete_task(warning+1));
        CHECK_VCOM(p.complete_task(warning) && p.take_output().empty()); // Never revoke across outstanding writes.
        p.accept(control(link::Type::WriteStatus,{{0,Value::uint(1)},{1,Value::uint(2)},{2,Value::uint(2)},{3,Value::uint(0)},{5,Value::uint(1)}}));
        CHECK_VCOM(p.take_output().empty());
        p.accept(control(link::Type::WriteStatus,{{0,Value::uint(2)},{1,Value::uint(1)},{2,Value::uint(1)},{3,Value::uint(0)},{5,Value::uint(2)}}));
        const auto commit=p.take_output(); CHECK_VCOM(commit.size()==1 && commit[0].type==link::Type::RetireAtBoundary);
        CHECK_VCOM(p.write(43,{9}) && p.take_output().empty() && !p.disconnected());
        // Repeated administrative preparation keeps the opaque warning token and cannot wedge a commit in flight.
        p.accept(control(link::Type::RetirementNotice,{{0,Value::uint(3)},{1,Value::text("Retiro temporal")}})); CHECK_VCOM(p.warning()==warning);
        p.accept(control(link::Type::RetireResult,{{0,Value::uint(warning)},{1,Value::boolean(false)},{2,Value::text("PORTGATE-BOUNDARY-CHANGED")}}));
        const auto resumed=p.take_output(); CHECK_VCOM(p.active() && resumed.size()==1 && resumed[0].type==link::Type::SerialData);
        p.accept(control(link::Type::WriteStatus,{{0,Value::uint(3)},{1,Value::uint(1)},{2,Value::uint(1)},{3,Value::uint(0)},{5,Value::uint(3)}}));
        CHECK_VCOM(p.complete_task(warning)); p.take_output();
        p.accept(control(link::Type::RetireResult,{{0,Value::uint(warning)},{1,Value::boolean(true)}})); CHECK_VCOM(p.disconnected() && !p.active());
        const auto events=p.take_events(); bool written=false,warning_event=false,disconnected=false;
        for(const auto& e:events) { written=written || (e.kind==VirtualComEvent::Kind::Written && e.request==42 && e.written==3 && e.detail.empty());
            warning_event=warning_event || e.kind==VirtualComEvent::Kind::Warning; disconnected=disconnected || e.kind==VirtualComEvent::Kind::Disconnected; }
        CHECK_VCOM(written && warning_event && disconnected);
    }
    {
        VirtualComProtocol p("COM15"); open(p); p.accept(control(link::Type::RetirementNotice,{{0,Value::uint(3)},{1,Value::text("Retiro")}}));
        p.accept(control(link::Type::RetirementNotice,{{0,Value::uint(3)},{3,Value::boolean(true)}}));
        CHECK_VCOM(!p.complete_task(p.warning()) && p.active());
        CHECK_VCOM(p.write(1,{4,5})); p.take_output();
        p.accept(control(link::Type::Revoked,{{1,Value::uint(2)},{2,Value::text("old")}})); CHECK_VCOM(p.active());
        p.accept(control(link::Type::Revoked,{{1,Value::uint(3)},{2,Value::text("PORTGATE-KILLED")}})); CHECK_VCOM(p.disconnected());
        CHECK_VCOM(!p.write(2,{6}));
    }
    {
        VirtualComProtocol p("COM15"); open(p); CHECK_VCOM(p.write(11,{7,8})); p.take_output();
        p.accept(control(link::Type::WriteStatus,{{0,Value::uint(1)},{1,Value::uint(2)},{2,Value::uint(1)},{3,Value::uint(1)}}));
        CHECK_VCOM(p.disconnected());
        VirtualComProtocol missing("COM15"); missing.accept(control(link::Type::Capabilities,{})); CHECK_VCOM(missing.disconnected());
        VirtualComProtocol gaps("COM15"); open(gaps);
        link::SerialData rx; rx.generation=3; rx.seq=1; rx.offset=0; rx.data={4}; gaps.accept(link::serial_data(1,rx)); CHECK_VCOM(gaps.disconnected());
    }
    {
        const auto f=frame("H|\\^&\rP|1\rO|1|sample\rR|1|X|123\rL|1|N\r");
        Lis01ResultsBoundary good; batch(good,f); CHECK_VCOM(good.complete());
        good.observe(true,{5}); CHECK_VCOM(!good.complete());
        Lis01ResultsBoundary lone; lone.observe(true,{4}); CHECK_VCOM(!lone.complete());
        Lis01ResultsBoundary no_ack; batch(no_ack,f,false); CHECK_VCOM(!no_ack.complete());
        auto bad=f; bad[8]^=1; Lis01ResultsBoundary corrupt; batch(corrupt,bad); CHECK_VCOM(!corrupt.complete());
        Lis01ResultsBoundary query; batch(query,frame("H|x\rQ|1|sample\rL|1|N\r")); CHECK_VCOM(!query.complete());
        Lis01ResultsBoundary order; batch(order,frame("H|x\rO|1|sample\rL|1|N\r")); CHECK_VCOM(!order.complete());
        Lis01ResultsBoundary partial; batch(partial,frame("H|x\rR|1|X|123\r")); CHECK_VCOM(!partial.complete());
        Lis01ResultsBoundary wrong_side; wrong_side.observe(false,{5}); CHECK_VCOM(!wrong_side.complete());
        Lis01ResultsBoundary coalesced; coalesced.observe(true,{5}); coalesced.observe(false,{6}); coalesced.observe(true,f); coalesced.observe(false,{6}); coalesced.observe(true,{4,5}); CHECK_VCOM(!coalesced.complete());
        auto boundary=std::make_unique<Lis01ResultsBoundary>(); auto* view=boundary.get(); VirtualComProtocol p("COM15",std::move(boundary)); open(p);
        p.accept(control(link::Type::RetirementNotice,{{0,Value::uint(3)},{1,Value::text("Retiro")}})); CHECK_VCOM(p.take_output().empty());
        // Receive an instrument ENQ, send legacy ACK, receive a valid results batch, ACK, then instrument EOT.
        std::uint64_t rx_seq=0,rx_offset=0;
        const auto receive=[&](const link::Bytes& bytes) { link::SerialData d;d.generation=3;d.seq=rx_seq++;d.offset=rx_offset;d.data=bytes;rx_offset+=bytes.size();p.accept(link::serial_data(1,d)); };
        receive({5}); CHECK_VCOM(p.write(1,{6})); p.take_output();
        p.accept(control(link::Type::WriteStatus,{{0,Value::uint(1)},{1,Value::uint(1)},{2,Value::uint(1)},{3,Value::uint(0)},{5,Value::uint(1)}}));
        for(auto byte:f) receive({byte}); CHECK_VCOM(!view->complete() && p.take_output().empty());
        CHECK_VCOM(p.write(2,{6})); p.take_output(); receive({4}); CHECK_VCOM(view->complete() && p.take_output().empty());
        p.accept(control(link::Type::WriteStatus,{{0,Value::uint(2)},{1,Value::uint(1)},{2,Value::uint(1)},{3,Value::uint(0)},{5,Value::uint(2)}}));
        const auto commits=p.take_output(); CHECK_VCOM(commits.size()==1 && commits[0].type==link::Type::RetireAtBoundary);
        const auto m=link::parse_map(commits[0]); CHECK_VCOM(m && *m->get(1)->as_uint()==rx_offset && *m->get(2)->as_uint()==rx_seq && *m->get(3)->as_uint()==2);
    }
    {
        AgentConfig c; c.agent_id="vcom"; c.ports.emplace_back(); auto& p=c.ports[0]; p.port_id="p"; p.device.path="COM1"; p.virtual_com="COM15"; p.controllers={{"vcom:COM15",10}};
        std::string error; CHECK_VCOM(decode_config(encode_config(c),error).has_value());
        p.virtual_com="COM1"; error.clear(); CHECK_VCOM(!decode_config(encode_config(c),error));
        p.virtual_com="COM0"; error.clear(); CHECK_VCOM(!decode_config(encode_config(c),error));
        p.virtual_com="COM15"; p.virtual_com_profile="guess-by-silence"; error.clear(); CHECK_VCOM(!decode_config(encode_config(c),error));
        p.virtual_com_profile="manual"; p.controllers={{"someone",10}}; error.clear(); CHECK_VCOM(!decode_config(encode_config(c),error));
    }
    std::cout<<checks<<" virtual COM checks, "<<failures<<" failures\n"; return failures?1:0;
}
