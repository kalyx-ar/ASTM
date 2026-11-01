#include "portgate/agent.hpp"
#include <algorithm>

namespace portgate {
namespace {
using cbor::Value;
std::uint64_t number(const Value& m, std::uint64_t key) {
    const auto* v=m.get(key); return v && v->as_uint()?*v->as_uint():UINT64_MAX;
}
bool flag(const Value& m, std::uint64_t key) {
    const auto* v=m.get(key); return v && v->as_bool() && *v->as_bool();
}
std::string text(const Value& m, std::uint64_t key) {
    const auto* v=m.get(key); return v && v->as_text()?*v->as_text():std::string();
}
}

// Called with the agent mutex. This is a declaration by the observer, never a protocol parser in PortGate.
void Agent::soft_kill(const std::shared_ptr<Connection>& connection, Port& port, const Value& map, std::uint64_t now) {
    const auto request=number(map,0);
    auto reply=[&](bool transferred,const std::string& code,const std::string& stage="completed") {
        send(connection,link::control(link::Type::SoftKillResult,port.index,Value::map({
            {0,Value::uint(request)},{1,Value::boolean(transferred)},{2,Value::text(code)},
            {3,Value::text(stage)},{4,Value::uint(port.config.virtual_com_guard_ms)}})));
    };
    if (!request || request==UINT64_MAX) return reply(false,"PORTGATE-SOFT-BAD-REQUEST");
    const auto previous=port.soft_results.find({connection->id,request});
    if(previous!=port.soft_results.end()) return reply(previous->second.first,previous->second.second);
    if(port.soft) {
        if(port.soft->requester==connection->id && port.soft->request==request) return reply(false,"","pending");
        return reply(false,"PORTGATE-SOFT-PENDING");
    }
    if(!port.arbiter->is_queued(connection->id)) return reply(false,"PORTGATE-SOFT-NOT-QUEUED");
    const auto observing=port.observation_drops.find(connection->id);
    if(!port.config.shared || !has_right(port,connection->controller_id,RightObserve) || observing==port.observation_drops.end())
        return reply(false,"PORTGATE-SOFT-NOT-OBSERVING-FLOW");
    if(observing->second!=connection->dropped_observed.load()) return reply(false,"PORTGATE-SOFT-OBSERVATION-GAP");
    const auto* epoch=map.get(1);
    if(!epoch || !epoch->as_bytes() || *epoch->as_bytes()!=cbor::Bytes(epoch_.begin(),epoch_.end()) ||
       number(map,2)!=port.arbiter->generation() || number(map,3)!=port.observe_seq)
        return reply(false,"PORTGATE-SOFT-STALE-OBSERVATION");
    if(!flag(map,4) || !flag(map,5) || text(map,6)!="no_pending_operations")
        return reply(false,"PORTGATE-SOFT-DECLARATION-REQUIRED");
    const auto owner=port.arbiter->owner();
    auto frontend=owner?connection_by_id(*owner):nullptr;
    if(!frontend || frontend->local_port!=port.index || !port.arbiter->may_write(*owner,port.arbiter->generation()))
        return reply(false,"PORTGATE-SOFT-NOT-VIRTUAL-OWNER");
    if(!port.open || port.pending_device || port.arbiter->closed() || port.arbiter->withdrawing())
        return reply(false,"PORTGATE-SOFT-PORT-UNAVAILABLE");
    const auto reason=text(map,7);
    if(reason.size()>500) return reply(false,"PORTGATE-SOFT-BAD-REASON");
    Port::SoftTakeover pending;
    pending.requester=connection->id; pending.owner=*owner; pending.request=request; pending.token=next_soft_++;
    pending.requester_name=connection->controller_id; pending.owner_name=frontend->controller_id;
    pending.generation=port.arbiter->generation(); pending.sequence=port.observe_seq; pending.tx_version=port.tx_version;
    pending.deadline=now+port.config.virtual_com_guard_ms; pending.reason=reason;
    port.soft=pending;
    Event event; event.kind="soft-kill-requested"; event.port_id=port.config.port_id;
    event.actor=connection->controller_id; event.target=frontend->controller_id;
    event.generation=pending.generation; event.reason=reason;
    event.detail="no_pending_operations; guard_ms="+std::to_string(port.config.virtual_com_guard_ms);
    broadcast(port,emit(event));
    reply(false,"","pending");
}

void Agent::finish_soft(Port& port,bool transferred,const std::string& code) {
    if(!port.soft) return;
    const auto pending=*port.soft; port.soft.reset();
    port.soft_results[{pending.requester,pending.request}]={transferred,code};
    while(port.soft_results.size()>64) port.soft_results.erase(port.soft_results.begin());
    if(!transferred && pending.pausing && port.arbiter->owner()==pending.owner && port.arbiter->generation()==pending.generation)
        if(auto owner=connection_by_id(pending.owner)) send(owner,link::control(link::Type::ResumeVirtualCom,port.index,
            Value::map({{0,Value::uint(pending.token)},{1,Value::uint(pending.generation)}})));
    if(auto requester=connection_by_id(pending.requester)) send(requester,link::control(link::Type::SoftKillResult,port.index,
        Value::map({{0,Value::uint(pending.request)},{1,Value::boolean(transferred)},{2,Value::text(code)},{3,Value::text("completed")}})));
    Event event; event.kind=transferred?"soft-kill-transferred":"soft-kill-cancelled"; event.port_id=port.config.port_id;
    event.actor=pending.requester_name; event.target=pending.owner_name;
    event.generation=pending.generation; event.reason=code; broadcast(port,emit(event));
}

void Agent::advance_soft(Port& port) {
    { std::lock_guard<std::mutex> lock(mutex_); if(!port.soft) return; }
    // No global lock while waiting for physical I/O. Readers account and route their bytes under read_mutex.
    std::unique_lock<std::mutex> writing(port.write_mutex,std::try_to_lock);
    if(!writing.owns_lock()) return; // A busy device must not stall housekeeping and leases on unrelated ports.
    std::unique_lock<std::mutex> reading(port.read_mutex);
    std::lock_guard<std::mutex> lock(mutex_);
    if(!port.soft) return;
    const auto pending=*port.soft;
    auto requester=connection_by_id(pending.requester);
    const auto observing=port.observation_drops.find(pending.requester);
    if(!requester || !requester->alive || !port.arbiter->is_queued(pending.requester) ||
       observing==port.observation_drops.end() || !has_right(port,requester->controller_id,RightObserve) ||
       observing->second!=requester->dropped_observed.load() || !port.config.shared || !port.open || port.pending_device ||
       port.arbiter->owner()!=pending.owner || port.arbiter->generation()!=pending.generation ||
       port.arbiter->withdrawing() || port.arbiter->closed()) {
        finish_soft(port,false,"PORTGATE-SOFT-INVALIDATED"); return;
    }
    const auto now=platform::monotonic_ms();
    if(pending.pausing) {
        if(now>=pending.deadline) finish_soft(port,false,"PORTGATE-SOFT-FRONTEND-TIMEOUT");
        return;
    }
    if(port.observe_seq!=pending.sequence || port.tx_version!=pending.tx_version) {
        finish_soft(port,false,"PORTGATE-SOFT-TRAFFIC"); return;
    }
    if(now<pending.deadline) return;
    auto frontend=connection_by_id(pending.owner);
    if(!frontend || !frontend->alive) { finish_soft(port,false,"PORTGATE-SOFT-FRONTEND-LOST"); return; }
    port.soft->pausing=true;
    port.soft->deadline=now+config_.security.handover_timeout_ms;
    send(frontend,link::control(link::Type::PauseVirtualCom,port.index,Value::map({
        {0,Value::uint(pending.token)},{1,Value::uint(pending.generation)}})));
}

void Agent::virtual_paused(const std::shared_ptr<Connection>& connection,Port& port,const Value& map) {
    std::unique_lock<std::mutex> writing(port.write_mutex);
    std::lock_guard<std::mutex> lock(mutex_);
    if(!port.soft || !port.soft->pausing || connection->local_port!=port.index || port.soft->owner!=connection->id ||
       number(map,0)!=port.soft->token || number(map,1)!=port.soft->generation) return;
    const auto pending=*port.soft;
    auto requester=connection_by_id(pending.requester);
    std::vector<ArbiterEvent> events;
    const auto observing=port.observation_drops.find(pending.requester);
    if(!flag(map,2) || !requester || !requester->alive || observing==port.observation_drops.end() ||
       observing->second!=requester->dropped_observed.load() || !has_right(port,requester->controller_id,RightObserve) ||
       !port.config.shared || !port.open || port.pending_device ||
       !port.arbiter->transfer(pending.owner,pending.generation,pending.requester,platform::monotonic_ms(),events)) {
        finish_soft(port,false,"PORTGATE-SOFT-INVALIDATED"); return;
    }
    finish_soft(port,true,"");
    writing.unlock();
    dispatch(port,events);
}
}
