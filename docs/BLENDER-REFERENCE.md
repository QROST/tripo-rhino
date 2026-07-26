# Lessons from the Tripo Blender extension

Reference snapshot:
[VAST-AI-Research/tripo-3d-for-blender at `d65412f` / `v0.7.7`](https://github.com/VAST-AI-Research/tripo-3d-for-blender/tree/d65412f4877f620aa2bb5027dc8cba087b79dabd).

Adopted product patterns:

- one host product per repository;
- a native in-host generation experience that does not require users to
  understand MCP;
- generation controls separated from task/history management;
- visible task IDs, progress, and manual reattachment/download;
- one version source and tag-shaped release-candidate packages;
- concise installation and first-use documentation.

Deliberately not copied:

- the unauthenticated localhost socket bridge and arbitrary code execution;
- returning a raw API key over the bridge;
- XOR/Base64 credential storage in the add-on directory;
- arbitrary URL download/import;
- retrying paid work without a durable caller operation ID, journal, and
  `outcome_unknown` state;
- tag publication without build/test gates;
- a source submodule accessed only through an SSH URL.

The Blender repository was studied as a product/repository reference only. No
upstream source code or assets were copied into this repository.
