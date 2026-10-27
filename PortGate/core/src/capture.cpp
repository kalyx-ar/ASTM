#include "portgate/capture.hpp"

#include <algorithm>

#include "portgate/platform.hpp"
#include "portgate/transport.hpp"

namespace portgate {

namespace {

using cbor::Value;

std::string hex(const std::string& text) {
    static const char* digits = "0123456789abcdef";
    std::string out;
    for (unsigned char c : text) {
        out.push_back(digits[c >> 4]);
        out.push_back(digits[c & 15]);
    }
    return out;
}

platform::Bytes frame(const CaptureRecord& r) {
    const auto body = cbor::encode(Value::map({{0, Value::uint(r.seq)},
                                               {1, Value::uint(r.key_id)},
                                               {2, Value::uint(r.time_ms)},
                                               {3, Value::bytes(r.nonce)},
                                               {4, Value::bytes(r.sealed)}}));
    platform::Bytes out{static_cast<std::uint8_t>(body.size() >> 24), static_cast<std::uint8_t>(body.size() >> 16),
                        static_cast<std::uint8_t>(body.size() >> 8), static_cast<std::uint8_t>(body.size())};
    out.insert(out.end(), body.begin(), body.end());
    return out;
}

std::uint64_t uint_or(const Value& map, std::uint64_t key) {
    const Value* v = map.get(key);
    return v && v->as_uint() ? *v->as_uint() : 0;
}

}  // namespace

std::vector<std::uint8_t> CaptureStore::aad(const std::string& agent_id, const std::string& port_id, std::uint64_t seq, std::uint32_t key_id) {
    return cbor::encode(Value::array({Value::text("portgate-capture/1"), Value::text(agent_id), Value::text(port_id), Value::uint(seq), Value::uint(key_id)}));
}

void CaptureStore::set_key(std::uint32_t key_id, std::vector<std::uint8_t> key) {
    std::lock_guard<std::mutex> lock(mutex_);
    key_id_ = key_id;
    key_ = std::move(key);
}

bool CaptureStore::has_key() const { return !key_.empty(); }

void CaptureStore::configure(const std::string& agent_id, const std::vector<PortConfig>& ports) {
    std::lock_guard<std::mutex> lock(mutex_);
    agent_id_ = agent_id;
    for (const auto& config : ports) {
        auto& p = port(config.port_id);
        p.limits = config.capture;
    }
}

CaptureStore::Port& CaptureStore::port(const std::string& port_id) {
    auto& p = ports_[port_id];
    if (p.path.empty()) {
        // Port ids are free text: hex keeps the file name portable.
        p.path = platform::join_path(directory_, "capture-" + hex(port_id) + ".bin");
        p.gaps_path = platform::join_path(directory_, "capture-" + hex(port_id) + ".gaps");
    }
    if (!p.loaded) load(p);
    return p;
}

void CaptureStore::load(Port& p) {
    p.loaded = true;
    platform::Bytes data;
    if (platform::read_file(p.path, data)) {
        std::size_t at = 0;
        while (at + 4 <= data.size()) {
            const std::size_t size = (std::size_t{data[at]} << 24) | (std::size_t{data[at + 1]} << 16) | (std::size_t{data[at + 2]} << 8) | data[at + 3];
            if (size == 0 || at + 4 + size > data.size()) break;  // partial tail of an interrupted write
            auto value = cbor::decode(data.data() + at + 4, size);
            at += 4 + size;
            if (!value || !value->as_map()) continue;
            CaptureRecord r;
            r.seq = uint_or(*value, 0);
            r.key_id = static_cast<std::uint32_t>(uint_or(*value, 1));
            r.time_ms = uint_or(*value, 2);
            if (const auto* n = value->get(3) ? value->get(3)->as_bytes() : nullptr) r.nonce = *n;
            if (const auto* s = value->get(4) ? value->get(4)->as_bytes() : nullptr) r.sealed = *s;
            p.records.push_back(std::move(r));
        }
        p.file_bytes = at;
    }
    platform::Bytes gaps;
    if (platform::read_file(p.gaps_path, gaps)) {
        auto value = cbor::decode(gaps.data(), gaps.size());
        const auto* list = value ? value->get(1) ? value->get(1)->as_array() : nullptr : nullptr;
        if (value && value->get(0)) p.next_seq = std::max<std::uint64_t>(p.next_seq, uint_or(*value, 0));
        if (list)
            for (const auto& g : *list) {
                const auto* reason = g.get(2) ? g.get(2)->as_text() : nullptr;
                p.gaps.push_back({uint_or(g, 0), uint_or(g, 1), reason ? *reason : std::string()});
            }
    }
    if (!p.records.empty()) p.next_seq = std::max(p.next_seq, p.records.back().seq + 1);
}

void CaptureStore::rewrite(Port& p) {
    platform::Bytes data;
    for (const auto& r : p.records) {
        const auto framed = frame(r);
        data.insert(data.end(), framed.begin(), framed.end());
    }
    std::string ignored;
    if (platform::write_file_atomic(p.path, data, ignored)) p.file_bytes = data.size();
    std::vector<Value> gaps;
    for (const auto& g : p.gaps) gaps.push_back(Value::array({Value::uint(g.from), Value::uint(g.to), Value::text(g.reason)}));
    // next_seq is kept with the gaps so a fully acknowledged capture never reuses sequence numbers.
    platform::write_file_atomic(p.gaps_path, cbor::encode(Value::map({{0, Value::uint(p.next_seq)}, {1, Value::array(std::move(gaps))}})), ignored);
}

void CaptureStore::add_gap(Port& p, std::uint64_t from, std::uint64_t to, const std::string& reason) {
    if (!p.gaps.empty() && p.gaps.back().reason == reason && p.gaps.back().to + 1 == from) p.gaps.back().to = to;
    else p.gaps.push_back({from, to, reason});
    if (p.gaps.size() > 1000) p.gaps.erase(p.gaps.begin());
}

void CaptureStore::enforce(Port& p) {
    const auto now = platform::utc_ms();
    std::size_t drop = 0;
    while (drop < p.records.size() && now - p.records[drop].time_ms > p.limits.retention_ms) ++drop;
    if (drop > 0) add_gap(p, p.records.front().seq, p.records[drop - 1].seq, "retention");
    std::size_t evicted = drop;
    if (p.file_bytes > p.limits.max_bytes) {
        // Over quota: keep the newest records that fit in three quarters of it.
        std::uint64_t kept = 0;
        std::size_t keep_from = p.records.size();
        while (keep_from > drop && kept + frame(p.records[keep_from - 1]).size() <= p.limits.max_bytes * 3 / 4) kept += frame(p.records[--keep_from]).size();
        if (keep_from > drop) add_gap(p, p.records[drop].seq, p.records[keep_from - 1].seq, "quota");
        evicted = keep_from;
    }
    if (evicted == 0) return;
    p.records.erase(p.records.begin(), p.records.begin() + static_cast<std::ptrdiff_t>(evicted));
    rewrite(p);
}

void CaptureStore::append(const std::string& port_id, const Value& record) {
    std::lock_guard<std::mutex> lock(mutex_);
    auto& p = port(port_id);
    if (!p.limits.enabled) return;
    const auto seq = p.next_seq++;
    CaptureRecord r;
    r.seq = seq;
    r.key_id = key_id_;
    r.time_ms = platform::utc_ms();
    std::string error;
    if (key_.empty() || !tls::seal(key_, aad(agent_id_, port_id, seq, key_id_), cbor::encode(record), r.nonce, r.sealed, error)) {
        add_gap(p, seq, seq, key_.empty() ? "no-key" : "seal-failed");
        rewrite(p);
        return;
    }
    const auto framed = frame(r);
    if (!platform::append_file(p.path, framed, error)) {
        add_gap(p, seq, seq, "write-failed");
        rewrite(p);
        return;
    }
    p.file_bytes += framed.size();
    p.records.push_back(std::move(r));
    enforce(p);
}

CapturePage CaptureStore::read(const std::string& port_id, std::uint64_t after, std::size_t max) {
    std::lock_guard<std::mutex> lock(mutex_);
    auto& p = port(port_id);
    CapturePage page;
    page.first = p.records.empty() ? 0 : p.records.front().seq;
    page.last = p.next_seq - 1;
    for (const auto& r : p.records) {
        if (r.seq <= after) continue;
        page.records.push_back(r);
        if (page.records.size() >= max) break;
    }
    for (const auto& g : p.gaps)
        if (g.to > after) page.gaps.push_back(g);
    return page;
}

bool CaptureStore::ack(const std::string& port_id, std::uint64_t upto, std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    auto& p = port(port_id);
    if (upto >= p.next_seq) {
        error = "cannot acknowledge records that were not written";
        return false;
    }
    p.records.erase(std::remove_if(p.records.begin(), p.records.end(), [&](const CaptureRecord& r) { return r.seq <= upto; }), p.records.end());
    p.gaps.erase(std::remove_if(p.gaps.begin(), p.gaps.end(), [&](const CaptureGap& g) { return g.to <= upto; }), p.gaps.end());
    rewrite(p);
    return true;
}

}  // namespace portgate
