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
| Fixed at install (`--server-cert sha256`, `--psk id:hex`) | Only a TLS client with that certificate fingerprint or PSK identity. |
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
| 0x42 SetConfig | 1 revision (assigned by the server), 2 config | 0 revision, 1 linkPort |
| 0x43 SetTls | 1 target (0 Link, 1 administration), 2 certificatePem, 3 privateKeyPem, 4 caPem, 5 pskIdentity, 6 psk bstr, 7 clear bool | 0 certificateSha256 |
| 0x44 ListDevices | — | array of {0 name, 1 friendlyName, 2 usbVid, 3 usbPid, 4 usbSerial, 5 location, 6 instanceId, 7 inUse} |
| 0x45 RevokePort | 1 portId | — |
| 0x46 ReopenPort | 1 portId (after a secure close) | — |
| 0x47 ReadEvents | 1 afterId, 2 max (≤ 1000) | 0 events [Event], 1 lastId |
| 0x48 Restart | — (restarts Link with the configuration in force) | 1 linkPort |
| 0x49 UnblockController | 1 controllerId | — |

`AdminEvent` (0x4E, agent → every authorized admin connection) carries one Event as it is recorded.

Rules:

1. `SetConfig` validates the whole configuration, starts the new Link endpoint and only then saves it. If the new configuration cannot start, the previous one keeps running and the answer is `PORTGATE-APPLY-FAILED`. Nothing is half-applied.
2. The agent keeps only the configuration in force; versions and rollback are the server's. To roll back, the server sends a complete previous configuration.
3. The agent always starts with the last installed configuration. It never returns to "not configured" by itself; a damaged state stops the service with an error (recovery: reinstall with operating-system administrator rights).
4. `SetTls` with a private key or PSK over a plain channel is refused (`PORTGATE-INSECURE-CHANNEL`). PSKs shorter than 16 bytes are refused (`PORTGATE-WEAK-PSK`). Material is validated by building a TLS server before it is saved; it is never returned by `GetConfig`.
5. Agent-initiated connections (`mode.initiator = 1`) are part of the configuration model but answer `PORTGATE-UNSUPPORTED` until P-09 implements the client role.

### Configuration map

| Key | Field |
|---|---|
| 0 | agentId tstr (required) |
| 1 | platformProfile tstr |
| 2 | mode {0 initiator (0 server, 1 agent), 1 serverHost, 2 serverPort, 3 restricted bool (default true), 4 adminRequiresTls bool} |
| 3 | link {0 host, 1 port (0 = ephemeral), 2 tls bool, 3 requireClientCertificate bool (default true), 4 minimumTls13 bool} |
| 4 | heartbeatMs [500, 600000] |
| 5 | maxUnitBytes [1024, 16 MiB] |
| 6 | security {0 killSwitch bool, 1 contest bool, 2 quarantineMs, 3 killCooldownMs, 4 contestBlockMs, 5 handoverIntervalMs, 6 handoverTimeoutMs} |
| 7 | ports [Port] |

Port: {0 portId, 1 instrumentId, 2 device {0 kind (0 serial, 1 tcp), 1 path, 2 baudRate, 3 dataBits, 4 parity (0 None, 1 Odd, 2 Even, 3 Mark, 4 Space), 5 stopBits, 6 flowControl (0 None, 1 RtsCts, 2 XonXoff), 7 host, 8 port}, 3 leaseMs [1000, 3600000], 4 maxQueuedRequests, 5 maxHoldMs (0 or ≥ leaseMs), 6 controllers [{0 id, 1 priority, 2 certificateSha256, 3 rights (1 kill switch, 2 contest)}], 7 maxWriteBytes}.

Unknown keys and out-of-range values are rejected with the path of the field.

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

## Events

Event map: {0 id, 1 timeUtcMs, 2 kind, 3 portId, 4 actor, 5 target, 6 generation, 7 reason, 8 untilUtcMs, 9 uncertain, 10 detail}. Kinds: `agent-start`, `config-applied`, `tls-updated`, `tls-cleared`, `restart`, `admin-denied`, `granted`, `revoked`, `admin-revoke`, `reopen`, `unblock`, `handover-requested`, `handover-yielded`, `handover-kept`, `kill-switch`, `kill-switch-denied`, `contest`, `secure-close`, `device-arrived`, `device-removed`.

The log is persistent (sealed records, size-bounded with one rotated file). Configuration changes are recorded as who, when and revision — never their content.
