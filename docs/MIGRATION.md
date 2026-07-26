# Fresh-repository migration

This repository was created without Git history from the clean
`QROST/TripoMCPs` snapshot
`f6300ceef6f61df60f438d6b546ae305af525ed2` on 2026-07-25.

The migration copied only tracked source, tests, and documentation. Generated
`bin/`, `obj/`, local notes, credentials, logs, and package outputs were not
copied. Rhino plug-in, panel, command, Grasshopper assembly, and component GUIDs
were preserved.

The copied runtime retains these compatibility identities:

- bridge protocol v2;
- host-control protocol v2;
- paid-operation journal schema v1;
- panel recovery schema v2;
- the existing `TripoMCP` local-data root and OS credential identities.

The same runtime snapshot also exists in the initial `tripo-revit` repository.
Until both products consume a single versioned runtime or an explicit sync
process exists, changes to credentials, journal, recovery, bridge contracts,
locks, staging manifests, or local-data layout must be reviewed and applied to
both repositories together. Do not rename those identities merely to make the
repositories appear independent: doing so can orphan unresolved paid
operations.

The original combined repository remains a rollback/reference source. This
migration does not authorize deleting it, publishing these repositories, or
changing their distribution license.
