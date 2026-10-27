# PortGate Link v1

Normative wire format between the PortGate agent and a controller (host). Implemented by `PortGate/core` (C++) and `dotnet/src/Atsm.Channels/SerialTunnel` (.NET). Shared test vectors: `conformance/link-v1/vectors.txt`.

## Unit

Every unit has a 12-byte header, big-endian, followed by `length` payload bytes.

| Offset | Size | Field | Value |
|---|---|---|---|
| 0 | 2 | magic | `0x41 0x4C` ("AL") |
| 2 | 1 | version | `0x01` |
| 3 | 1 | type | see below |
| 4 | 2 | flags | bit0 payload is CBOR, bit15 critical, others 0 |
| 6 | 2 | channel | 0 = agent control, n = port index (1-based, order of `Capabilities.ports`) |
| 8 | 4 | length | ≤ `maxUnitBytes` announced in `Hello` (default 65536) |

A receiver validates magic, version and length **before** allocating. An unknown type without bit15 is ignored; with bit15 it closes the connection after `Error`.

## Types and CBOR maps

CBOR (RFC 8949) with deterministic encoding: definite lengths, shortest integer encoding, map keys are small unsigned integers in ascending order. Unknown keys are ignored.

| Type | Name | Direction | Map |
|---|---|---|---|
| 0x01 | Hello | both | 0 version uint, 1 role tstr (`agent`/`controller`), 2 id tstr, 3 agentEpoch bstr(16) (agent), 4 platform tstr, 5 maxUnitBytes uint |
| 0x02 | Capabilities | agent → host | 0 ports: array of {0 portId tstr, 1 instrumentId tstr, 2 ownership uint (0 none, 1 advisory, 2 enforced), 3 traceCapture uint (0 none, 1 ram, 2 durable), 4 serialSignals bool, 5 maxRecordBytes uint, 6 queuedAcquisition bool, 7 device map in force (spec/admin-v1.md § Configuration map), so a controller can compare serial settings} |
| 0x10 | Acquire | host → agent | 0 requestId uint, 1 priority int, 2 deadlineMs uint |
| 0x11 | Queued | agent → host | 0 requestId uint, 1 position uint |
| 0x12 | Granted | agent → host | 0 requestId uint, 1 generation uint, 2 transferId bstr(16), 3 leaseMs uint, 4 pendingTxBytes uint, 5 uncertainTx bool, 6 unownedRxBytes uint |
| 0x13 | Denied | agent → host | 0 requestId uint, 1 code tstr |
| 0x14 | Renew | host → agent | 0 requestId uint, 1 generation uint (answered with Granted, same generation) |
| 0x15 | Release | host → agent | 1 generation uint |
| 0x16 | Revoked | agent → host | 1 generation uint, 2 code tstr |
| 0x18 | ConfirmTransfer | host → agent | 1 generation uint, 2 transferId bstr(16) (opens write admission) |
| 0x20 | SerialData | both | binary, not CBOR: seq u64, offset u64, generation u32, then raw bytes |
| 0x21 | WriteStatus | agent → host | 0 seq uint, 1 accepted uint, 2 written uint, 3 result uint (0 completed, 1 partial, 2 unknown, 3 rejected), 4 code tstr |
| 0x22 | PortEvent | agent → host | 0 kind uint (0 opened, 1 closed, 2 faulted, 3 overflow, 4 discontinuity), 1 code tstr, 2 detail tstr |
| 0x24 | ObservedData | agent → observer | binary, not CBOR: seq u64, offset u64, generation u32, direction u8 (0 instrument → host, 1 host → instrument), then raw bytes |
| 0x7E | Heartbeat | both | 0 monotonicMs uint |
| 0x7F | Error | both | 0 code tstr, 1 requestId uint, 2 detail tstr |

Observers enable themselves with `Observe`/`ObserveResult` (0x30/0x31, spec/admin-v1.md § Semaphore extensions) and receive `ObservedData`. `traceCapture` is 2 on ports with sealed capture and 0 otherwise; the capture is read through administration (spec/admin-v1.md § Sealed capture). Connections opened by the agent toward the server (agent-initiated mode) carry the same units: see spec/admin-v1.md § Agent-initiated connections.

## Rules

1. The agent sends `Hello` and `Capabilities` on accept. The controller answers `Hello` with its `controllerId` before any other unit.
2. A controller may write only after `Granted` **and** `ConfirmTransfer` with the same generation and transferId.
3. Every `SerialData` from the host carries the generation; the agent answers each with `WriteStatus`. A write with a stale generation, from a connection that is not the owner, or before `ConfirmTransfer`, is `rejected` and no byte reaches the port.
4. A lease not renewed within `leaseMs` expires; the agent sends `Revoked` and grants the next queued request (priority, then FIFO). The priority never preempts a valid owner.
5. Bytes received from the port while no controller owns it are counted (and discarded in v1); the next `Granted` reports them in `unownedRxBytes`.
6. A new agent start produces a new `agentEpoch`; generations restart and leases of a previous epoch are invalid.
7. `seq` and `offset` are per port and per direction since agent start.

## Security

`Plain` mode carries the `controllerId` from `Hello` without authentication; it is accepted only when listed in the port configuration and must be used only on trusted networks.

`Tls` mode (Mbed TLS 4.1 LTS in PortGate) requires TLS 1.2 or 1.3 (`minimumVersion` may force 1.3) and, by default, a client certificate that chains to `clientCa`. The `controllerId` of `Hello` must equal the certificate subject CN, or the certificate SHA-256 (DER) must equal the `certificateSha256` configured for that controller; otherwise the agent answers `Error` `PORTGATE-IDENTITY-MISMATCH` and closes. A `Tls` endpoint never falls back to plain text, and an agent built without TLS refuses to start such an endpoint.
