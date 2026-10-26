#include "portgate/events.hpp"

#include <algorithm>

#include "portgate/platform.hpp"

namespace portgate {

namespace {

using cbor::Value;

constexpr const char* kCurrent = "events.log";
constexpr const char* kRotated = "events.1.log";

std::string text_or(const Value& map, std::uint64_t key) {
    const Value* v = map.get(key);
    const auto* t = v ? v->as_text() : nullptr;
    return t ? *t : std::string();
}

std::uint64_t uint_or(const Value& map, std::uint64_t key) {
    const Value* v = map.get(key);
    return v && v->as_uint() ? *v->as_uint() : 0;
}

}  // namespace

Value Event::to_cbor() const {
    std::map<std::uint64_t, Value> m{{0, Value::uint(id)}, {1, Value::uint(time_ms)}, {2, Value::text(kind)}};
    if (!port_id.empty()) m.emplace(3, Value::text(port_id));
    if (!actor.empty()) m.emplace(4, Value::text(actor));
    if (!target.empty()) m.emplace(5, Value::text(target));
    if (generation != 0) m.emplace(6, Value::uint(generation));
    if (!reason.empty()) m.emplace(7, Value::text(reason));
    if (until_ms != 0) m.emplace(8, Value::uint(until_ms));
    if (uncertain) m.emplace(9, Value::boolean(true));
    if (!detail.empty()) m.emplace(10, Value::text(detail));
    return Value::map(std::move(m));
}

std::optional<Event> Event::from_cbor(const Value& value) {
    if (!value.as_map() || !value.get(0) || !value.get(2)) return std::nullopt;
    Event e;
    e.id = uint_or(value, 0);
    e.time_ms = uint_or(value, 1);
    e.kind = text_or(value, 2);
    e.port_id = text_or(value, 3);
    e.actor = text_or(value, 4);
    e.target = text_or(value, 5);
    e.generation = static_cast<std::uint32_t>(uint_or(value, 6));
    e.reason = text_or(value, 7);
    e.until_ms = uint_or(value, 8);
    const Value* uncertain = value.get(9);
    e.uncertain = uncertain && uncertain->as_bool() && *uncertain->as_bool();
    e.detail = text_or(value, 10);
    return e;
}

EventLog::EventLog(std::string directory, std::uint64_t max_file_bytes, std::size_t memory)
    : directory_(std::move(directory)), max_file_bytes_(max_file_bytes), memory_(memory) {}

bool EventLog::open(std::string& error) {
    if (!platform::ensure_directory(directory_, error)) return false;
    std::lock_guard<std::mutex> lock(mutex_);
    recent_.clear();
    load_file(platform::join_path(directory_, kRotated));
    load_file(platform::join_path(directory_, kCurrent));
    next_id_ = recent_.empty() ? 1 : recent_.back().id + 1;
    return true;
}

void EventLog::load_file(const std::string& path) {
    platform::Bytes data;
    if (!platform::read_file(path, data)) return;
    std::size_t at = 0;
    while (at + 4 <= data.size()) {
        const std::size_t size = (std::size_t{data[at]} << 24) | (std::size_t{data[at + 1]} << 16) | (std::size_t{data[at + 2]} << 8) | data[at + 3];
        if (size == 0 || at + 4 + size > data.size()) break;  // damaged or partial tail
        platform::Bytes sealed(data.begin() + static_cast<std::ptrdiff_t>(at + 4), data.begin() + static_cast<std::ptrdiff_t>(at + 4 + size));
        at += 4 + size;
        platform::Bytes plain;
        std::string ignored;
        if (!platform::unprotect(sealed, plain, ignored)) continue;
        auto value = cbor::decode(plain.data(), plain.size());
        auto event = value ? Event::from_cbor(*value) : std::nullopt;
        if (!event) continue;
        recent_.push_back(std::move(*event));
        if (recent_.size() > memory_) recent_.pop_front();
    }
}

Event EventLog::append(Event event, std::string* error) {
    std::lock_guard<std::mutex> lock(mutex_);
    event.id = next_id_++;
    event.time_ms = platform::utc_ms();
    recent_.push_back(event);
    if (recent_.size() > memory_) recent_.pop_front();

    std::string failure;
    platform::Bytes sealed;
    const auto plain = cbor::encode(event.to_cbor());
    if (platform::protect(plain, sealed, failure)) {
        const auto current = platform::join_path(directory_, kCurrent);
        if (platform::file_size(current) + sealed.size() + 4 > max_file_bytes_)
            platform::replace_file(current, platform::join_path(directory_, kRotated), failure);
        platform::Bytes record{static_cast<std::uint8_t>(sealed.size() >> 24), static_cast<std::uint8_t>(sealed.size() >> 16),
                               static_cast<std::uint8_t>(sealed.size() >> 8), static_cast<std::uint8_t>(sealed.size())};
        record.insert(record.end(), sealed.begin(), sealed.end());
        failure.clear();
        platform::append_file(current, record, failure);
    }
    if (error) *error = failure;
    return event;
}

std::vector<Event> EventLog::read(std::uint64_t after_id, std::size_t max) const {
    std::lock_guard<std::mutex> lock(mutex_);
    std::vector<Event> out;
    for (const auto& event : recent_) {
        if (event.id <= after_id) continue;
        out.push_back(event);
        if (out.size() >= max) break;
    }
    return out;
}

std::optional<Event> EventLog::find(std::uint64_t id) const {
    std::lock_guard<std::mutex> lock(mutex_);
    auto it = std::find_if(recent_.begin(), recent_.end(), [&](const Event& e) { return e.id == id; });
    return it == recent_.end() ? std::nullopt : std::optional<Event>(*it);
}

std::uint64_t EventLog::last_id() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return next_id_ - 1;
}

}  // namespace portgate
