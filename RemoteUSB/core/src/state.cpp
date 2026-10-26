#include "remoteusb/state.hpp"

#include "remoteusb/platform.hpp"

namespace remoteusb {

namespace {

using cbor::Value;

constexpr const char* kStateFile = "state.bin";

std::string text_or(const Value& map, std::uint64_t key) {
    const Value* v = map.get(key);
    const auto* t = v ? v->as_text() : nullptr;
    return t ? *t : std::string();
}

bool bool_or(const Value& map, std::uint64_t key) {
    const Value* v = map.get(key);
    return v && v->as_bool() && *v->as_bool();
}

Value encode_tls(const TlsMaterial& t) {
    std::map<std::uint64_t, Value> m;
    if (!t.certificate_pem.empty()) m.emplace(0, Value::text(t.certificate_pem));
    if (!t.private_key_pem.empty()) m.emplace(1, Value::text(t.private_key_pem));
    if (!t.ca_pem.empty()) m.emplace(2, Value::text(t.ca_pem));
    if (!t.psk_identity.empty()) m.emplace(3, Value::text(t.psk_identity));
    if (!t.psk.empty()) m.emplace(4, Value::bytes(t.psk));
    return Value::map(std::move(m));
}

TlsMaterial decode_tls(const Value* v) {
    TlsMaterial t;
    if (!v || !v->as_map()) return t;
    t.certificate_pem = text_or(*v, 0);
    t.private_key_pem = text_or(*v, 1);
    t.ca_pem = text_or(*v, 2);
    t.psk_identity = text_or(*v, 3);
    if (const Value* psk = v->get(4); psk && psk->as_bytes()) t.psk = *psk->as_bytes();
    return t;
}

}  // namespace

std::optional<AgentState> StateStore::load(std::string& error) const {
    platform::Bytes sealed;
    if (!platform::read_file(platform::join_path(directory_, kStateFile), sealed)) return std::nullopt;
    platform::Bytes plain;
    if (!platform::unprotect(sealed, plain, error)) return std::nullopt;
    auto value = cbor::decode(plain.data(), plain.size(), {8, 65536});
    if (!value || !value->as_map() || !value->get(0) || value->get(0)->as_uint() != std::optional<std::uint64_t>(1)) {
        error = "the stored state is damaged or of an unknown version";
        return std::nullopt;
    }
    AgentState state;
    state.configured = bool_or(*value, 1);
    if (state.configured) {
        const Value* config = value->get(2);
        auto decoded = config ? decode_config(*config, error) : std::nullopt;
        if (!decoded) {
            error = "stored configuration: " + error;
            return std::nullopt;
        }
        state.config = std::move(*decoded);
    }
    if (const Value* revision = value->get(3); revision && revision->as_uint()) state.revision = *revision->as_uint();
    if (const Value* admin = value->get(4); admin && admin->as_map()) {
        state.admin.claimed = bool_or(*admin, 0);
        state.admin.server_id = text_or(*admin, 1);
        state.admin.certificate_sha256 = text_or(*admin, 2);
        state.admin.address = text_or(*admin, 3);
        state.admin.restricted = bool_or(*admin, 4);
    }
    state.link_tls = decode_tls(value->get(5));
    state.admin_tls = decode_tls(value->get(6));
    return state;
}

bool StateStore::save(const AgentState& state, std::string& error) const {
    if (!platform::ensure_directory(directory_, error)) return false;
    std::map<std::uint64_t, Value> m{{0, Value::uint(1)},
                                     {1, Value::boolean(state.configured)},
                                     {3, Value::uint(state.revision)},
                                     {4, Value::map({{0, Value::boolean(state.admin.claimed)},
                                                     {1, Value::text(state.admin.server_id)},
                                                     {2, Value::text(state.admin.certificate_sha256)},
                                                     {3, Value::text(state.admin.address)},
                                                     {4, Value::boolean(state.admin.restricted)}})},
                                     {5, encode_tls(state.link_tls)},
                                     {6, encode_tls(state.admin_tls)}};
    if (state.configured) m.emplace(2, encode_config(state.config));
    platform::Bytes sealed;
    if (!platform::protect(cbor::encode(Value::map(std::move(m))), sealed, error)) return false;
    return platform::write_file_atomic(platform::join_path(directory_, kStateFile), sealed, error);
}

}  // namespace remoteusb
