# PortGate administration v1 and semaphore extensions

Normative extension of [PortGate Link v1](link-v1.md): same unit framing (magic `AL`, version 1) and deterministic CBOR maps with unsigned integer keys. Design and decisions: [ADMINISTRACION-REMOTA](../Documentacion/ADMINISTRACION-REMOTA.md).

## Administration endpoint

- Default port **28240** (`portgate run --admin host:port`). TLS and plain text share the port: a first byte `0x16` (TLS handshake record) selects TLS, anything else plain Link framing.
- TLS uses the agent certificate, generated at first start (ECDSA P-256, self-signed, CN = agent name) and kept in the protected state, or the material installed with `SetTls` target 1, and/or a PSK. The fingerprint is printed at start (`PORTGATE-ADMIN <port> <sha256>`) and reported by `GetStatus`.
- The administering client may present any certificate: it is identified by the SHA-256 of that certificate (pinning), not chained to a CA. Without a certificate or PSK, its identity is its address.
- After TCP/TLS setup the agent sends `Hello` (role `agent`). The client answers `Hello` {0 version 1, 1 role `admin`, 2 serverId}.

### Who may administer

| State | Rule |
|---|---|
| Fresh install, nothing fixed | Any client. The first `SetConfig` records its identity (certificate fingerprint, else address). |
| Fixed at install (`--server-cert sha256`, `--psk id:hex`) | Only a TLS client with that certificate fingerprint or PSK identity. With `--server host:port` the agent dials that server (agent-initiated) and the same pin or PSK authenticates it. |
| Configured, `mode.restricted = true` | Only the recorded identity (certificate fingerprint, PSK identity, or address when it administered in plain text). |
| Configured, `mode.restricted = false` | Any client; the last configurator is recorded. |
| `mode.adminRequiresTls = true` | Plain-text administration is refused. It can only be set over TLS. |

A refused `Hello` gets `AdminResult` {2 `PORTGATE-ADMIN-NOT-AUTHORIZED`} and the connection is closed; the attempt is an `admin-denied` event.

### Requests and results

Every request carries `0 requestId`. The answer is `AdminResult` (0x4F) {0 requestId, 1 ok bool, 2 code tstr (when not ok), 3 detail tstr, 4 body}.

| Type | Request | Body |
|---|---|---|
| 0x40 GetStatus | — | 0 agentId, 1 platform, 2 version, 4 revision, 5 configured, 6 claimed, 7 restricted, 8 linkPort, 9 adminCertificateSha256, 10 ports [{0 portId, 1 instrumentId, 2 device, 3 open, 4 owner, 5 generation, 6 queued, 7 closed, 8 quarantineMs}], 11 linkTls, 12 linkError |
| 0x41 GetConfig | — | 0 revision, 1 config (when configured) |
| 0x42 SetConfig | 1 revision (assigned by the server), 2 config | 0 revision, 1 linkPort, 2 hot bool |
| 0x43 SetTls | 1 target (0 Link, 1 administration), 2 certificatePem, 3 privateKeyPem, 4 caPem, 5 pskIdentity, 6 psk bstr, 7 clear bool | 0 certificateSha256 |
| 0x44 ListDevices | — | array of {0 name, 1 friendlyName, 2 usbVid, 3 usbPid, 4 usbSerial, 5 location, 6 instanceId, 7 inUse} |
| 0x45 RevokePort | 1 portId | — |
| 0x46 ReopenPort | 1 portId (after a secure close) | — |
| 0x47 ReadEvents | 1 afterId, 2 max (≤ 1000) | 0 events [Event], 1 lastId |
| 0x48 Restart | — (restarts Link with the configuration in force) | 1 linkPort |
| 0x49 UnblockController | 1 controllerId | — |
| 0x4A SetCaptureKey | 1 keyId uint (> 0), 2 key bstr (32 bytes); TLS only, else `PORTGATE-INSECURE-CHANNEL` | — |
| 0x4B ReadCapture | 1 portId, 2 afterSeq, 3 max (≤ 1000) | 0 records [{0 seq, 1 keyId, 2 timeMs, 3 nonce, 4 sealed}], 1 first, 2 last, 3 gaps [[from, to, reason]], 4 agentId |
| 0x4C AckCapture | 1 portId, 2 uptoSeq (custody confirmed: deletes up to it) | — |

`AdminEvent` (0x4E, agent → every authorized admin connection) carries one Event as it is recorded.

Rules:

1. `SetConfig` validates the whole configuration, starts the new Link endpoint and only then saves it. If the new configuration cannot start, the previous one keeps running and the answer is `PORTGATE-APPLY-FAILED`. Nothing is half-applied.
   When the new configuration differs only in devices (serial settings, path, TCP endpoint), it is applied **hot**: each changed port is reopened by its own thread, the Link endpoint and every connection stay up, the owner keeps its tenure and receives `PortEvent` kind 4 `PORTGATE-PORT-RECONFIGURED` (bytes in flight have an unknown outcome), and new connections see the new device in `Capabilities`. Any other change (ports, controllers, leases, security, Link endpoint) restarts Link.
2. The agent keeps only the configuration in force; versions and rollback are the server's. To roll back, the server sends a complete previous configuration.
3. The agent always starts with the last installed configuration. It never returns to "not configured" by itself; a damaged state stops the service with an error (recovery: reinstall with operating-system administrator rights).
4. `SetTls` with a private key or PSK over a plain channel is refused (`PORTGATE-INSECURE-CHANNEL`). PSKs shorter than 16 bytes are refused (`PORTGATE-WEAK-PSK`). Material is validated by building a TLS server before it is saved; it is never returned by `GetConfig`.
5. Agent-initiated mode (`mode.initiator = 1`): see § Agent-initiated connections.

### Configuration map

| Key | Field |
|---|---|
| 0 | agentId tstr (required) |
| 1 | platformProfile tstr |
| 2 | mode {0 initiator (0 server, 1 agent), 1 serverHost, 2 serverPort, 3 restricted bool (default true), 4 adminRequiresTls bool, 5 serverCertificateSha256 tstr, 6 outboundTls bool (default true when 5 is set), 7 connections [1, 16] (default 2)} |
| 3 | link {0 host, 1 port (0 = ephemeral), 2 tls bool, 3 requireClientCertificate bool (default true), 4 minimumTls13 bool} |
| 4 | heartbeatMs [500, 600000] |
| 5 | maxUnitBytes [1024, 16 MiB] |
| 6 | security {0 killSwitch bool, 1 contest bool, 2 quarantineMs, 3 killCooldownMs, 4 contestBlockMs, 5 handoverIntervalMs, 6 handoverTimeoutMs} |
| 7 | ports [Port] |

Port: {0 portId, 1 instrumentId, 2 device {0 kind (0 serial, 1 tcp: the agent connects to the instrument, 2 tcp-listen: the instrument connects to the agent), 1 path, 2 baudRate, 3 dataBits, 4 parity (0 None, 1 Odd, 2 Even, 3 Mark, 4 Space), 5 stopBits, 6 flowControl (0 None, 1 RtsCts, 2 XonXoff), 7 host (tcp-listen: local address, default 0.0.0.0), 8 port, 9 dtr bool (default true), 10 rts bool (default true; ignored with RtsCts)}, 3 leaseMs [1000, 3600000], 4 maxQueuedRequests, 5 maxHoldMs (0 or ≥ leaseMs), 6 controllers [{0 id, 1 priority, 2 certificateSha256, 3 rights (1 kill switch, 2 contest, 4 observe)}], 7 maxWriteBytes, 8 capture {0 enabled bool, 1 maxBytes [65536, 1 GiB] (default 16 MiB), 2 retentionMs [60000, 90 days] (default 7 days)}, 9 shared bool (default false: exclusive)}.

Unknown keys and out-of-range values are rejected with the path of the field.

## Agent-initiated connections

For agents behind isolated networks or without inbound ports. Enabled by `mode.initiator = 1` or, before any configuration, by installing with `--server host:port`.

1. The agent keeps `mode.connections` TCP connections to `serverHost:serverPort` (one before configuration) and opens no inbound Link endpoint (`GetStatus` and `SetConfig` report Link port 0). The administration endpoint keeps listening where `--admin` says (use `127.0.0.1` for a local UI only).
2. TLS (`outboundTls`, or install with `--server-cert`/`--psk`): the agent is the TLS client. It presents its own certificate (the one of the administration endpoint) and authenticates the server by `serverCertificateSha256` or, without it, by the administration PSK; it never trusts a CA. A pin and a PSK are not offered together (the pin wins). Outbound TLS without either is refused (`PORTGATE-OUTBOUND-IDENTITY`). The server pins the agent certificate (reported by `GetStatus` and printed at start).
3. The **server speaks first**: its `Hello` gives the connection its role. Role `admin` runs the administration protocol (same authorization rules, identity = server certificate fingerprint, PSK identity or address). Role `controller` runs Link: the agent answers `Hello` and `Capabilities`, and the controller id declared by the server is accepted because the server was authenticated (the port's controller list still applies; kill switch and contest need TLS). A controller connection before any configuration is answered with `Error` `PORTGATE-NOT-CONFIGURED`.
4. A connection that ends is dialed again (1 s, doubling up to 30 s while it fails). Changing between server- and agent-initiated modes restarts Link; connections already serving keep their role until they end.

Server side (.NET): `PortGateListener` accepts the agent connections; `PortGateAdminClient.OpenAsync` and `PortGateChannel.OpenAsync` give them their role over the accepted stream.

## Semaphore extensions (Link endpoint)

| Type | Direction | Map |
|---|---|---|
| 0x17 RequestHandover | controller → agent | 0 requestId, 1 reason |
| 0x19 HandoverAsked | agent → owner | 0 handoverId, 1 requester, 2 reason, 3 timeoutMs |
| 0x1A HandoverAnswer | owner → agent | 0 handoverId, 1 yield bool |
| 0x1B HandoverResult | agent → requester | 0 requestId, 1 yielded bool, 2 code |
| 0x1C KillSwitch | controller → agent | 0 requestId, 1 reason |
| 0x1D Contest | controller → agent | 0 requestId, 1 killEventId, 2 reason, 3 secureClose bool |
| 0x1E SecurityResult | agent → requester | 0 requestId, 1 eventId (kill switch or contest accepted; refusals are `Error` with the requestId) |
| 0x30 Observe | controller → agent | 0 requestId, 1 enable bool (needs right 4, else `PORTGATE-OBSERVE-NOT-ALLOWED`) |
| 0x31 ObserveResult | agent → requester | 0 requestId, 1 observing bool, 2 code, 3 traffic bool (the port is shared) |
| 0x25 PortState | agent → observer | 0 open bool, 1 owner, 2 generation, 3 queued, 4 secureClosed bool, 5 quarantined bool, 6 shared bool (on Observe and whenever it changes) |
| 0x23 SecurityEvent | agent → every controller of the port | 0 eventId, 1 kind, 2 portId, 3 actor, 4 target, 5 generation, 6 reason, 7 untilUtcMs, 8 uncertain, 9 timeUtcMs |

Cooperative handover request:

1. The requester must be queued on the port (`Acquire` answered with `Queued`); otherwise `PORTGATE-HANDOVER-NOT-QUEUED`. One pending request per port (`PORTGATE-HANDOVER-PENDING`) and one per controller per `handoverIntervalMs` (`PORTGATE-HANDOVER-RATE`).
2. The owner answers within `handoverTimeoutMs`. Yield: the owner gets `Revoked` `PORTGATE-HANDOVER` and the queue grants the port (priority, then order). Keep: `PORTGATE-HANDOVER-KEPT`. No answer: the owner **keeps** the port (`PORTGATE-HANDOVER-NO-ANSWER`).

Kill switch (optional, `security.killSwitch`):

1. Requires TLS with an authenticated peer (verified client certificate or PSK) — `PORTGATE-KILL-NEEDS-TLS` —, the right on the port — `PORTGATE-KILL-NOT-ALLOWED` —, an owner and `killCooldownMs` since the previous kill by the same controller. Refusals are `Error` units and `kill-switch-denied` events.
2. The owner gets `Revoked` `PORTGATE-KILLED`; the port stays in quarantine (nobody is granted) for `quarantineMs`, which is also the contest window. Then the queue grants it normally: killing never hands the port to the killer.
3. A `kill-switch` SecurityEvent goes to every controller of the port and to the event log, with actor and address, target, generation, `uncertain` (a write in progress or of unknown outcome) and the contest deadline.

Contest (optional, `security.contest`):

1. Same TLS requirement and its own right; within the window; not by the killer itself.
2. Effect: every connection of the killer is dropped and its `Hello` refused (`PORTGATE-CONTROLLER-BLOCKED`) for `contestBlockMs` or until `UnblockController`. The interface is not reset.
3. With `secureClose`, the port is closed for everyone (`PORTGATE-SECURE-CLOSED`) until an administrator sends `ReopenPort`.

`maxHoldMs` (per port): while other controllers wait, an owner whose continuous tenure reached the limit gets `Revoked` `PORTGATE-MAX-HOLD` instead of a renewal.

Every observer follows the semaphore and the port state (`PortState`, SecurityEvent). Only on a shared port (`shared`) does it also receive a read-only copy of every byte, owned or not, as `ObservedData` (spec/link-v1.md); on an exclusive port the traffic belongs to the owner alone. Observing never grants writing or ownership.

No observer slows the owner: every connection has its own writer thread and outbox, so the port thread, the arbitration and the other connections never wait on a peer. Copies for an observer more than 256 KiB behind are discarded and show as a jump in the ObservedData `seq`; a connection more than 4 MiB behind on any other unit is closed. `observe-start`/`observe-stop` go as SecurityEvent to the controllers of the port and to the event log: there is no hidden tap.

## Sealed capture

Per port and optional (`capture.enabled`). The agent seals each read and each successful write with AES-256-GCM, using the key the host provisions with `SetCaptureKey`. The key is kept in the machine-sealed state, never leaves the agent and never appears in events. AAD = CBOR array ["portgate-capture/1", agentId, portId, seq, keyId], so a record cannot be moved to another agent, port or position. Plaintext: CBOR {0 epoch, 1 portId, 2 direction (0 instrument → host, 1 host → instrument), 3 generation, 4 offset, 5 timeUtcMs, 6 data, 7 owner}.

1. `SetConfig` enabling capture without a key is refused with `PORTGATE-CAPTURE-KEY-MISSING`.
2. The agent never decrypts and keeps no plaintext on disk. `ReadCapture` returns sealed records; the host opens them and deduplicates by (agentId, portId, seq).
3. Records are deleted only by `AckCapture` (event `capture-ack`), by retention or by quota (keeping the newest within three quarters of `maxBytes`). Every loss is an explicit gap with reason `no-key`, `seal-failed`, `write-failed`, `quota`, `retention` or `backlog`.
5. The port thread only queues the record (at most 4 MiB in clear, in memory); a writer thread seals and stores it, and wipes the clear copy once sealed. Records beyond the queue are not kept and become a `backlog` gap. Neither sealing, the disk nor `ReadCapture`/`AckCapture` delay the owner. `ReadCapture` first waits (up to 1 s) for what is queued.
4. Capture is evidence for diagnosis and reconciliation; the agent stays transparent and never answers the instrument.

## Events

Event map: {0 id, 1 timeUtcMs, 2 kind, 3 portId, 4 actor, 5 target, 6 generation, 7 reason, 8 untilUtcMs, 9 uncertain, 10 detail}. Kinds: `agent-start`, `config-applied`, `tls-updated`, `tls-cleared`, `restart`, `admin-denied`, `granted`, `revoked`, `admin-revoke`, `reopen`, `unblock`, `handover-requested`, `handover-yielded`, `handover-kept`, `kill-switch`, `kill-switch-denied`, `contest`, `secure-close`, `device-arrived`, `device-removed`, `observe-start`, `observe-stop`, `capture-key`, `capture-ack`.

The log is persistent (sealed records, size-bounded with one rotated file). Configuration changes are recorded as who, when and revision — never their content.
