#include <chrono>
#include "worker.hpp"

namespace portgate::vcom {
Worker::Worker(std::string name, std::function<bool(const VirtualComEvent&)> sink)
    :name_(std::move(name)),protocol_(name_),sink_(std::move(sink)) {}
Worker::~Worker() { close(); }
bool Worker::start() {
    std::unique_lock<std::mutex> lock(mutex_);
    std::string error; transport_=connect_virtual_com(name_,error);
    if (!transport_) return false;
    protocol_.begin(); if(!flush()) return false;
    thread_=std::thread([this]{run();});
    changed_.wait_for(lock,std::chrono::seconds(5),[this]{return protocol_.active() || protocol_.disconnected();});
    return protocol_.active();
}
bool Worker::flush() {
    auto output=protocol_.take_output(); auto events=protocol_.take_events();
    // Deliver the terminal bytes and the disconnect state before releasing the physical tenure.
    for(const auto& event:events) if(!sink_(event)) {
        protocol_.fault("PORTGATE-VCOM-FRONTEND-FAULT");
        for(const auto& fault:protocol_.take_events()) sink_(fault);
        break;
    }
    for(const auto& unit:output) {
        if(protocol_.disconnected() && unit.type!=link::Type::Release) continue;
        const auto bytes=link::encode(unit);
        if(!transport_ || !transport_->send_all(bytes.data(),bytes.size())) {
            protocol_.fault("PORTGATE-VCOM-IPC-LOST");
            for(const auto& event:protocol_.take_events()) sink_(event);
            changed_.notify_all(); return false;
        }
    }
    changed_.notify_all(); return true;
}
void Worker::run() {
    link::Decoder decoder; std::uint8_t bytes[16384];
    auto renewed=std::chrono::steady_clock::now();
    for(;;) {
        const auto n=transport_->receive(bytes,sizeof(bytes),100);
        std::lock_guard<std::mutex> lock(mutex_);
        if(stopping_) break;
        if(n<=0 && n!=-2) { protocol_.fault("PORTGATE-VCOM-IPC-LOST"); flush(); break; }
        if(n>0) {
            std::vector<link::Unit> units;
            if(decoder.feed(bytes,static_cast<std::size_t>(n),units)!=link::DecodeError::None) { protocol_.fault("PORTGATE-VCOM-BAD-LINK"); flush(); break; }
            for(const auto& unit:units) {
                protocol_.accept(unit);
            }
        }
        const auto now=std::chrono::steady_clock::now();
        if(now-renewed>=std::chrono::milliseconds(protocol_.lease_ms()/3)) { protocol_.renew(); renewed=now; }
        if(!flush() || protocol_.disconnected()) break;
    }
    transport_->shutdown();
}
bool Worker::write(std::uint64_t id,const link::Bytes& bytes) {
    std::lock_guard<std::mutex> lock(mutex_);
    return !stopping_ && protocol_.write(id,bytes) && flush();
}
void Worker::fault(const std::string& reason) {
    std::lock_guard<std::mutex> lock(mutex_);
    protocol_.fault(reason); flush(); if(transport_) transport_->shutdown();
}
void Worker::close() {
    { std::lock_guard<std::mutex> lock(mutex_);
      if(stopping_) return;
      stopping_=true;
      if(transport_) { protocol_.close(); flush(); transport_->shutdown(); }
    }
    if(thread_.joinable()) thread_.join();
}
}
