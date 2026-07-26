# Architecture Decision Records

English | [简体中文](./README.zh-CN.md)

ADRs record durable design choices for `tripo-rhino`. They are the change-control
surface for trust boundaries, front doors, and credential ownership. Update the
ADR (or supersede it) before contradicting implementation.

| ID | Title | Status |
| --- | --- | --- |
| [0001](./0001-dual-front-doors-and-sidecar-credentials.md) | Dual front doors (host UI + MCP) and sidecar-only API credentials | Accepted; §3.3 Grasshopper scope partially superseded by 0002 |
| [0002](./0002-recoverable-grasshopper-components.md) | Explicit, recoverable Grasshopper components over the Rhino sidecar | Accepted |

Related non-ADR design locks:

- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) — current trust boundaries and stage sequence
- [`../SECURITY.md`](../SECURITY.md) — credential, journal, bridge, and artifact rules
- [`../MATERIALS-DESIGN.md`](../MATERIALS-DESIGN.md) — OBJ baked-diffuse materials and import modes

The English body of each ADR is normative. A Chinese summary inside an ADR is
provided for reading convenience only.
