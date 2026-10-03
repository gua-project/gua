# Windows candidate execution record

Retained actual execution outputs for source `0a5cbc83fbfd5ce9a465508644f96aced5694d4a`, private version `0.0.0-ci`, Unity 6000.5.3f1, Windows x64 Mono/PhysX. These outputs are copied from the desktop runs, not newly executed by reading this file. Full local evidence ZIP SHA-256: `69f70c018ebad700d8d884f253eb3e2b12d9c940e8cf3697104a1198c48872ea`. Binary archive remains desktop evidence; the forthcoming candidate CI must independently retain its actual archives and route outputs.

Commands: `scripts/verify-unity-distribution-player.ps1 -PackageDirectory <offline-feed> -PlayerPath <TGZ-built-player> -SourceCommit 0a5cbc83fbfd5ce9a465508644f96aced5694d4a -OutputDirectory <evidence>`; the portable client Editor arguments were `editor Assets/Scenes/GuaUnityFixture.unity <TGZ-consumer-project> <evidence> <same-source> <installed-6000.5.3f1-Unity.exe>`. Existing `verify-unity-spatial-player.ts` executed the same precompiled provider via TS/built-MCP/exact-NuGet consumers. The clean client was explicitly rebuilt after restoring the selected mutation to prevent incremental reuse.

## Installed archive and external package identity

```json
{
  "installedCache": "C:\\Users\\testk\\AppData\\Local\\Temp\\gua-unity129-8a07e199f4a1413a9a1520179e9fd805\\consumer\\Library\\PackageCache\\com.link1345.gua@4d00858a9396",
  "matchedFiles": 42,
  "version": "0.0.0-ci",
  "archiveSha256": "a9a79360ee116f467614e62ab9a52ddb82bc0eec2d40cc74c34270e99151cc97",
  "guaLibraries": [
    "Gua.Core/0.0.0-ci",
    "Gua.Runtime/0.0.0-ci",
    "Gua.Testing/0.0.0-ci",
    "Gua.Testing.Unity/0.0.0-ci"
  ],
  "sourceCommit": "0a5cbc83fbfd5ce9a465508644f96aced5694d4a",
  "packageMetadata": "equivalent except Unity-added _fingerprint",
  "projectLibraries": 0,
  "archive": "C:\\Users\\testk\\AppData\\Local\\Temp\\gua-unity129-8a07e199f4a1413a9a1520179e9fd805\\com.link1345.gua-0.0.0-ci-win-x64.tgz",
  "nativeOverrides": "absent"
}
```

## Actual standalone Player operation

```json
{
  "mode": "player",
  "sourceCommit": "0a5cbc83fbfd5ce9a465508644f96aced5694d4a",
  "version": {
    "ProtocolSchemaVersion": "2",
    "CoreVersion": "0.0.0-ci",
    "RuntimeVersion": "0.0.0-ci",
    "GodotPluginVersion": null,
    "AbiVersion": 1,
    "BuildId": "0a5cbc83fbfd5ce9a465508644f96aced5694d4a",
    "Capabilities": [
      "semantic_ui_tree_v2",
      "detailed_semantic_state_v1",
      "semantic_actions_v2",
      "context_reset_v1",
      "diagnostics_v1",
      "version_v1",
      "capture_screenshot_v1",
      "virtual_clock_v1",
      "semantic_game_input_v1",
      "semantic_game_input_search_v1",
      "semantic_game_input_metadata_v1",
      "raw_keyboard_input_v1",
      "raw_pointer_input_v1",
      "raw_gamepad_input_v1",
      "text_input_v1",
      "game_input_lease_v1",
      "world_object_tree_v1",
      "agent_projection_v1",
      "semantic_lint_v1",
      "observe_v1"
    ],
    "AdapterVersions": {
      "unity": "0.0.0-ci"
    }
  },
  "requestId": "1",
  "completion": {
    "RequestId": 1,
    "Action": 1,
    "Succeeded": true,
    "Error": 0,
    "NodeId": "start",
    "Value": "",
    "Sensitive": false,
    "SessionEpoch": 1,
    "FrameSequence": 11,
    "Revision": 3
  },
  "missingTarget": "NodeNotFound",
  "initialScreen": "title",
  "finalScreen": "loading",
  "trace": "C:\\Users\\testk\\AppData\\Local\\Temp\\gua-unity129-8a07e199f4a1413a9a1520179e9fd805\\tracked-client-player-evidence-2\\trace\\65621d1ee28a4846958c57b37e3715f6",
  "report": "C:\\Users\\testk\\AppData\\Local\\Temp\\gua-unity129-8a07e199f4a1413a9a1520179e9fd805\\tracked-client-player-evidence-2\\report.html"
}
```

## Actual Editor Play Mode operation

```json
{
  "mode": "editor",
  "sourceCommit": "0a5cbc83fbfd5ce9a465508644f96aced5694d4a",
  "version": {
    "ProtocolSchemaVersion": "2",
    "CoreVersion": "0.0.0-ci",
    "RuntimeVersion": "0.0.0-ci",
    "GodotPluginVersion": null,
    "AbiVersion": 1,
    "BuildId": "0a5cbc83fbfd5ce9a465508644f96aced5694d4a",
    "Capabilities": [
      "semantic_ui_tree_v2",
      "detailed_semantic_state_v1",
      "semantic_actions_v2",
      "context_reset_v1",
      "diagnostics_v1",
      "version_v1",
      "capture_screenshot_v1",
      "virtual_clock_v1",
      "world_object_tree_v1",
      "agent_projection_v1",
      "semantic_lint_v1",
      "observe_v1"
    ],
    "AdapterVersions": {
      "unity": "0.0.0-ci"
    }
  },
  "requestId": "1",
  "completion": {
    "RequestId": 1,
    "Action": 1,
    "Succeeded": true,
    "Error": 0,
    "NodeId": "start",
    "Value": "",
    "Sensitive": false,
    "SessionEpoch": 1,
    "FrameSequence": 2,
    "Revision": 2
  },
  "missingTarget": "NodeNotFound",
  "initialScreen": "title",
  "finalScreen": "loading",
  "trace": "C:\\Users\\testk\\AppData\\Local\\Temp\\gua-unity129-8a07e199f4a1413a9a1520179e9fd805\\tracked-client-editor-evidence-2\\trace\\97ce86b534f347e19bbe1a9a73609663",
  "report": "C:\\Users\\testk\\AppData\\Local\\Temp\\gua-unity129-8a07e199f4a1413a9a1520179e9fd805\\tracked-client-editor-evidence-2\\report.html"
}
```

## Current restored dependency notice closure (new helper, separate from earlier archive)

```json
{
  "guaLicense": "LICENSE",
  "packages": [
    {
      "sha256": "004f7be9f00ed73d02459af917549f0066eb6f3ce364407ccc4c1a5423779abc",
      "package": "Gua.Core/0.0.0-ci",
      "notices": [],
      "license": "MIT"
    },
    {
      "sha256": "eda7053005af5dc8815d22406daf2d41a4017b3ba7cf4e4b7b7750116eff09fe",
      "package": "Gua.Runtime/0.0.0-ci",
      "notices": [],
      "license": "MIT"
    },
    {
      "sha256": "9463c891bfd03be08936a5a35365377bbc24e48eb2ca47fbb5db12e96d8edf8d",
      "package": "Microsoft.Bcl.AsyncInterfaces/10.0.0",
      "notices": [
        "Microsoft.Bcl.AsyncInterfaces-10.0.0-THIRD-PARTY-NOTICES.TXT",
        "Microsoft.Bcl.AsyncInterfaces-10.0.0-MIT.txt"
      ],
      "license": "MIT"
    },
    {
      "sha256": "b00451e91d016fbec091ad1e361f3a7015e1d91d4047f7e48a74455b2a673d79",
      "package": "System.Buffers/4.6.1",
      "notices": [
        "System.Buffers-4.6.1-MIT.txt"
      ],
      "license": "MIT"
    },
    {
      "sha256": "d1a788c4b144067f41a026bddd454d8a4765aa03e5dc1d6799808b43d62ef409",
      "package": "System.IO.Pipelines/10.0.0",
      "notices": [
        "System.IO.Pipelines-10.0.0-THIRD-PARTY-NOTICES.TXT",
        "System.IO.Pipelines-10.0.0-MIT.txt"
      ],
      "license": "MIT"
    },
    {
      "sha256": "26078aeb758c9ae985e8bf851f973026061da6a5eb4837204d0c2d2204c72955",
      "package": "System.Memory/4.6.3",
      "notices": [
        "System.Memory-4.6.3-MIT.txt"
      ],
      "license": "MIT"
    },
    {
      "sha256": "5f6a7f53af3465f92beb6da873ebe0e496206c313313b98badee4355a6b25937",
      "package": "System.Runtime.CompilerServices.Unsafe/6.1.2",
      "notices": [
        "System.Runtime.CompilerServices.Unsafe-6.1.2-MIT.txt"
      ],
      "license": "MIT"
    },
    {
      "sha256": "3abb20a803df1238e8f82cf3ac0ce42935cdbf7557c36766dd6a67fae44708cf",
      "package": "System.Text.Encodings.Web/10.0.0",
      "notices": [
        "System.Text.Encodings.Web-10.0.0-THIRD-PARTY-NOTICES.TXT",
        "System.Text.Encodings.Web-10.0.0-MIT.txt"
      ],
      "license": "MIT"
    },
    {
      "sha256": "67200c213df240325acd800e6c0957992706b21183f4c55981898ba87c880aad",
      "package": "System.Text.Json/10.0.0",
      "notices": [
        "System.Text.Json-10.0.0-THIRD-PARTY-NOTICES.TXT",
        "System.Text.Json-10.0.0-MIT.txt"
      ],
      "license": "MIT"
    },
    {
      "sha256": "1abc92c7517a021ead7e79d0b7f3c70be75bce0fac7cfe3b38e157d32245ff1a",
      "package": "System.Threading.Tasks.Extensions/4.6.3",
      "notices": [
        "System.Threading.Tasks.Extensions-4.6.3-MIT.txt"
      ],
      "license": "MIT"
    }
  ]
}
```

## Actual spatial host completion

```json
{"physicsBatches":3,"backend":"PhysX","version":"6000.5.3f1"}
```

## Actual package-only spatial client results

```json
{
  "info": {
    "SchemaVersion": "spatial-host-r1",
    "DocumentType": "advertisement",
    "Provider": {
      "SchemaVersion": "spatial-r1",
      "DocumentType": "provider",
      "ProviderId": "unity-transport",
      "SpaceId": "fixture",
      "SpaceEpoch": 1,
      "WorldSpace": "world3d",
      "Basis": {
        "X": {
          "X": 1,
          "Y": 0,
          "Z": 0
        },
        "Y": {
          "X": 0,
          "Y": 1,
          "Z": 0
        },
        "Z": {
          "X": 0,
          "Y": 0,
          "Z": 1
        }
      },
      "Up": {
        "X": 0,
        "Y": 1,
        "Z": 0
      },
      "Unit": {
        "Label": "fixture-unit",
        "MetersPerUnit": null
      },
      "Precision": {
        "Representation": "binary32",
        "Reason": "backend_error_unmeasured",
        "AbsoluteError": null
      },
      "Operations": [
        "raycast",
        "overlap",
        "sweep"
      ],
      "Shapes": [
        "sphere",
        "capsule",
        "box"
      ],
      "Policies": [
        "solid"
      ],
      "Consistencies": [
        "bestEffort",
        "samePhysicsSample"
      ],
      "Engine": {
        "Name": "Unity",
        "Version": "6000.5.3f1",
        "Backend": "PhysX",
        "BackendVersion": "unknown"
      },
      "Limits": {
        "MaxQueriesPerBatch": 64,
        "MaxHitsPerQuery": 2,
        "MaxDeadlineMs": 1000
      }
    },
    "Budgets": {
      "MaxQueueDepth": 4,
      "QueryDeadlineMs": 1000,
      "MaxBatchWorkTimeMs": 1000
    }
  },
  "result": {
    "SchemaVersion": "spatial-host-r1",
    "DocumentType": "batchResult",
    "BatchId": 100,
    "Items": [
      {
        "RequestId": 101,
        "QueryId": "ray",
        "State": "completed",
        "Reason": null,
        "Result": {
          "SchemaVersion": "spatial-host-r1",
          "DocumentType": "result",
          "RequestId": 101,
          "SessionEpoch": 1,
          "QueryId": "ray",
          "SpaceId": "fixture",
          "SpaceEpoch": 1,
          "Kind": "raycast",
          "Status": "completed",
          "Error": null,
          "Outcome": "hit",
          "Coverage": {
            "State": "complete",
            "LoadedRegion": {
              "Min": {
                "X": -20,
                "Y": -20,
                "Z": -20
              },
              "Max": {
                "X": 20,
                "Y": 20,
                "Z": 20
              }
            },
            "Reason": null
          },
          "Truncated": false,
          "Sample": {
            "PhysicsSampleId": "3d0559eb7016498eaffc7155b81a5560:26",
            "ClockId": "unity-transport",
            "ObservedFromMs": 541.2984,
            "ObservedToMs": 541.594,
            "QueryPolicyRevision": 1,
            "Tick": null,
            "WorldSnapshot": null
          },
          "Hits": [
            {
              "Relation": "unknown",
              "Missing": {
                "collisionRef": "not_published",
                "distance": "not_observed",
                "normal": "not_observed",
                "position": "not_observed",
                "worldObjectId": "not_published"
              },
              "Position": null,
              "Distance": null,
              "Normal": null,
              "CollisionRef": null,
              "WorldObjectId": null
            }
          ],
          "Nearest": "returnedHits",
          "OriginInside": "unknown",
          "InitialOverlap": null,
          "Motion": null
        }
      },
      {
        "RequestId": 102,
        "QueryId": "overlap",
        "State": "completed",
        "Reason": null,
        "Result": {
          "SchemaVersion": "spatial-host-r1",
          "DocumentType": "result",
          "RequestId": 102,
          "SessionEpoch": 1,
          "QueryId": "overlap",
          "SpaceId": "fixture",
          "SpaceEpoch": 1,
          "Kind": "overlap",
          "Status": "completed",
          "Error": null,
          "Outcome": "detected",
          "Coverage": {
            "State": "complete",
            "LoadedRegion": {
              "Min": {
                "X": -20,
                "Y": -20,
                "Z": -20
              },
              "Max": {
                "X": 20,
                "Y": 20,
                "Z": 20
              }
            },
            "Reason": null
          },
          "Truncated": false,
          "Sample": {
            "PhysicsSampleId": "3d0559eb7016498eaffc7155b81a5560:26",
            "ClockId": "unity-transport",
            "ObservedFromMs": 541.2984,
            "ObservedToMs": 541.8167,
            "QueryPolicyRevision": 1,
            "Tick": null,
            "WorldSnapshot": null
          },
          "Hits": [
            {
              "Relation": "unknown",
              "Missing": {
                "collisionRef": "not_published",
                "distance": "not_observed",
                "normal": "not_observed",
                "position": "not_observed",
                "worldObjectId": "not_published"
              },
              "Position": null,
              "Distance": null,
              "Normal": null,
              "CollisionRef": null,
              "WorldObjectId": null
            }
          ],
          "Nearest": null,
          "OriginInside": null,
          "InitialOverlap": null,
          "Motion": null
        }
      },
      {
        "RequestId": 103,
        "QueryId": "sweep",
        "State": "completed",
        "Reason": null,
        "Result": {
          "SchemaVersion": "spatial-host-r1",
          "DocumentType": "result",
          "RequestId": 103,
          "SessionEpoch": 1,
          "QueryId": "sweep",
          "SpaceId": "fixture",
          "SpaceEpoch": 1,
          "Kind": "sweep",
          "Status": "completed",
          "Error": null,
          "Outcome": "blocked",
          "Coverage": {
            "State": "complete",
            "LoadedRegion": {
              "Min": {
                "X": -20,
                "Y": -20,
                "Z": -20
              },
              "Max": {
                "X": 20,
                "Y": 20,
                "Z": 20
              }
            },
            "Reason": null
          },
          "Truncated": false,
          "Sample": {
            "PhysicsSampleId": "3d0559eb7016498eaffc7155b81a5560:26",
            "ClockId": "unity-transport",
            "ObservedFromMs": 541.2984,
            "ObservedToMs": 542.0245,
            "QueryPolicyRevision": 1,
            "Tick": null,
            "WorldSnapshot": null
          },
          "Hits": [
            {
              "Relation": "unknown",
              "Missing": {
                "collisionRef": "not_published",
                "distance": "not_observed",
                "normal": "not_observed",
                "position": "not_observed",
                "worldObjectId": "not_published"
              },
              "Position": null,
              "Distance": null,
              "Normal": null,
              "CollisionRef": null,
              "WorldObjectId": null
            }
          ],
          "Nearest": null,
          "OriginInside": null,
          "InitialOverlap": "notDetected",
          "Motion": null
        }
      }
    ]
  },
  "trace": {
    "SchemaVersion": 1,
    "TraceId": "586428b6032545e3ae177f636368aa73",
    "CaptureMode": "recent",
    "SavePolicy": "always",
    "Profile": "debug",
    "PrimaryOutcome": "passed",
    "Finalized": true,
    "CollectionClock": "collector-monotonic:586428b6032545e3ae177f636368aa73",
    "StartedAt": "2026-10-03T01:15:23.4255819\u002B00:00",
    "LastSequence": 6,
    "Quality": {
      "DetailStopped": false,
      "EvictedSteps": 0,
      "DroppedEvents": 0,
      "Issues": []
    }
  },
  "report": "C:\\Users\\testk\\AppData\\Local\\Temp\\gua-unity129-8a07e199f4a1413a9a1520179e9fd805\\spatial-route-evidence-2\\unity-player-nhYd2o\\package-consumer\\spatial-report.html"
}
```

## Archive payload file hashes

```json
{
  "files": [
    {
      "path": "LICENSE",
      "sha256": "0a429d07fb4c5a3cb532c89c639a763b87986f53552362d3f205ad1bd3a04c2e"
    },
    {
      "path": "package.json",
      "sha256": "519900b0c8e13197bf51075d2c8ecdae9b08b09382530fd57f21bd6c80995eb8"
    },
    {
      "path": "Documentation~/index.md",
      "sha256": "5846424ff694929aa6e918ad3661fddb5cd5005d0df1e580d13f307a0f0460fb"
    },
    {
      "path": "Editor/Gua.Unity.Editor.dll",
      "sha256": "35a3168dbcdda2e8e942467731822e0e7f2499e48ec9f1af7683db280b387ac8"
    },
    {
      "path": "Editor/Gua.Unity.Editor.dll.meta",
      "sha256": "f9cf041f6539f8cff9051fa883a6cb9cf77189859028908c17be97effb8732f8"
    },
    {
      "path": "notices/Gua-MIT.LICENSE.txt",
      "sha256": "0a429d07fb4c5a3cb532c89c639a763b87986f53552362d3f205ad1bd3a04c2e"
    },
    {
      "path": "notices/Gua.Testing-0.0.0-ci-LICENSE",
      "sha256": "ad6d900e61ee0d15f2a50f49255207b36825427ea1734b65c932011d53f33602"
    },
    {
      "path": "notices/Gua.Testing-0.0.0-ci-Viewer.LICENSES.txt",
      "sha256": "677015e8beca6424448eadb7f514023a9ea5bcf1dd729b57560a803c53b87689"
    },
    {
      "path": "notices/Microsoft.Bcl.AsyncInterfaces-10.0.0-THIRD-PARTY-NOTICES.TXT",
      "sha256": "4462f49e15da16ab142250cc1120453ff676931280ac797045e01b22a390f601"
    },
    {
      "path": "notices/System.Buffers-4.6.1-MIT.txt",
      "sha256": "34ebc934ec73f51b4e97caa8ee1fdbad344663742c2c822ad7c2d283061b574d"
    },
    {
      "path": "notices/System.IO.Pipelines-10.0.0-THIRD-PARTY-NOTICES.TXT",
      "sha256": "4462f49e15da16ab142250cc1120453ff676931280ac797045e01b22a390f601"
    },
    {
      "path": "notices/System.Memory-4.6.3-MIT.txt",
      "sha256": "a69a06f714e15a98f36f10cc4e6994a310997152757e97df67d231523f58d26d"
    },
    {
      "path": "notices/System.Runtime.CompilerServices.Unsafe-6.1.2-MIT.txt",
      "sha256": "96c6bbc8b1331893219451fd0a8054732579bf8b9b4b19db600b18716920f8cf"
    },
    {
      "path": "notices/System.Text.Encodings.Web-10.0.0-THIRD-PARTY-NOTICES.TXT",
      "sha256": "4462f49e15da16ab142250cc1120453ff676931280ac797045e01b22a390f601"
    },
    {
      "path": "notices/System.Text.Json-10.0.0-THIRD-PARTY-NOTICES.TXT",
      "sha256": "4462f49e15da16ab142250cc1120453ff676931280ac797045e01b22a390f601"
    },
    {
      "path": "notices/System.Threading.Tasks.Extensions-4.6.3-MIT.txt",
      "sha256": "acab940c1417be538b7625e5e03bca7a2514a7d9b4a1143eb95b19bcdbbbcf34"
    },
    {
      "path": "Runtime/link.xml",
      "sha256": "654fcfb92d6ac799827b8f0ecd95f00aa18c33e18dd70cfa518aace4811256c9"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Core.dll",
      "sha256": "39268cf92cdc3fa2f3569e4239d234c704f5e8e1b08cb216d293babfd5dfa6f2"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Core.dll.meta",
      "sha256": "9ff9df64591a10db3d68fb27775464fe367e7e31e15a4016fc195f3f49e6ec8e"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Runtime.dll",
      "sha256": "ab3f8d1da16a601b72582db54e21afc162357522752f54c1e4631130490a3ab3"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Runtime.dll.meta",
      "sha256": "120ea0fbb814b3646aa972adaa136388ffa5e3e867a8ba70357bdaf5ef266c16"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Unity.Bootstrap.dll",
      "sha256": "d516e92ffef8a3359f922d9ca98f5c8057f8efc6236e6e5baf0f5fff1170f2bd"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Unity.Bootstrap.dll.meta",
      "sha256": "942da46628a55bfbb03e1bcdd522e1d6f2c156c2aad595e884b116eb3eaaa91a"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Unity.dll",
      "sha256": "3c554eb8f2e6909405a61ef889b8f762aafdf62dd7129974b7e26de8e600951f"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Unity.dll.meta",
      "sha256": "4a3524692cc91830d26132e831ebac1aa6cc3b33f5cb11d4d7a64ef897cb6c51"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Unity.TMP.dll",
      "sha256": "b2ec536280e16a6e653f2776fdfb0d8c518f64c494f5d40b64811fbea66d561c"
    },
    {
      "path": "Runtime/Plugins/Managed/Gua.Unity.TMP.dll.meta",
      "sha256": "fc259bdc893744ace3e0921f573715eaa765c17531b1bb6629fcfd593fc414e4"
    },
    {
      "path": "Runtime/Plugins/Managed/Microsoft.Bcl.AsyncInterfaces.dll",
      "sha256": "0108e3141daa64a82b7411cc98d6664e169d41e637e4a5af2d19e926733efdcb"
    },
    {
      "path": "Runtime/Plugins/Managed/System.Buffers.dll",
      "sha256": "aa4d023d710b7dab42ccce349491a4f5e2cee51ee858fac5e4856a72f174d433"
    },
    {
      "path": "Runtime/Plugins/Managed/System.IO.Pipelines.dll",
      "sha256": "69a61fd51a202def22b9ef4aef126eddd4303d2212e0aa1fa13cdb525fe4f96b"
    },
    {
      "path": "Runtime/Plugins/Managed/System.Memory.dll",
      "sha256": "9052f3b6f64b7b70e54fa417e46027de1320de0713881fccbcb427ecedda287a"
    },
    {
      "path": "Runtime/Plugins/Managed/System.Runtime.CompilerServices.Unsafe.dll",
      "sha256": "fd67abc4c4af10affbcd42fd68a2a7bc361b26714afe6965d4d0d139459d8297"
    },
    {
      "path": "Runtime/Plugins/Managed/System.Text.Encodings.Web.dll",
      "sha256": "c3a81ad7f5310c185c4c6eea9b1d10e1030511773ee7e0d4ab1e488c218e8c45"
    },
    {
      "path": "Runtime/Plugins/Managed/System.Text.Json.dll",
      "sha256": "84a2a238b1c10086c2c9ea37b7d72ef1ac6f6a42e9ac19177df26e093ec6e6a3"
    },
    {
      "path": "Runtime/Plugins/Managed/System.Threading.Tasks.Extensions.dll",
      "sha256": "bb1655039bad6141a4739e39793e787545380f8cb59de6c431d1c92a67bd351b"
    },
    {
      "path": "Runtime/Plugins/Windows/x86_64/gua_runtime.dll",
      "sha256": "a65ae077c10cf96a4ca1098f4e31ab4348c2a3211691da4ef0e40712a6802664"
    },
    {
      "path": "Runtime/Plugins/Windows/x86_64/gua_runtime.dll.meta",
      "sha256": "5457a4f8a7421933ff38ddba499e40c6e31fd399be4a73e67266fcb218c58c7c"
    },
    {
      "path": "Runtime/Plugins/Windows/x86_64/gua.dll",
      "sha256": "e73e06e6c5b5455e0645daea7fc6d29a28a874ceea247d8605cb0564999ec2c2"
    },
    {
      "path": "Runtime/Plugins/Windows/x86_64/gua.dll.meta",
      "sha256": "97730e19f5ee861d23e4e9f981614421e7062c10d188c9d5791913bd26b81b04"
    },
    {
      "path": "Samples~/Runtime UI Fixture/GuaRuntimeUiSample.cs",
      "sha256": "34d28f2ca49c4a8eb421ef67ffd4c888396b86c684de1bb0fbb468708b1fb03d"
    },
    {
      "path": "Samples~/Runtime UI Fixture/GuaRuntimeUiSample.unity",
      "sha256": "cbf0e9b73e2095083a17e3699de8e552f2b9426edd9a470924f05c853bea8c37"
    },
    {
      "path": "Samples~/Runtime UI Fixture/README.md",
      "sha256": "e6cf9f21d63e6b068eafb99bd805f95d4fd634b101552b3545e1fdd579d50755"
    }
  ],
  "rid": "win-x64",
  "version": "0.0.0-ci",
  "sourceCommit": "0a5cbc83fbfd5ce9a465508644f96aced5694d4a",
  "producer": "C:\\Users\\testk\\AppData\\Local\\Temp\\gua-unity129-8a07e199f4a1413a9a1520179e9fd805\\producer"
}
```

## Selected violation detection

Disposable actual Player client changed only `completion.Value.RequestId != request` to `completion.Value.RequestId != request + 1`. Required correlated completion assertion output:

```text
Unhandled exception. System.Exception: Correlated successful click completion missing
   at Program.<Main>$(String[] args) in C:\Users\testk\AppData\Local\Temp\gua-unity-client-0a2ce11fa0da4b8d91a1bb49deea3c81\Program.cs:line 37
   at Program.<Main>(String[] args)
```

Disposable Bad.Dep/1.0.0 archive had no nuspec license metadata. Notice helper rejected it before a valid manifest:

```text
Dependency license metadata is absent: Bad.Dep/1.0.0
```

Wrong candidate source pin (`ffffffffffffffffffffffffffffffffffffffff`) was rejected as `Candidate package identity mismatch.` before macOS assembly. No mutation or invalid package is submitted.

## Boundaries

This earlier Windows archive exposes the Microsoft own-MIT notice omission fixed by this PR; its native/managed execution proves the measured routes, not full redistribution readiness. The new helper notice manifest above is a separate positive closure check. The candidate CI must build the corrected archive and pass its own exact-source acceptance. Linux/macOS Player, non-Windows Editor, other features/backends/patches and independent #130 integration conditions are unverified. Full binary/Viewer artifacts stay outside git; this durable record provides operation, identity, hashes and failure outputs to reviewers. No browser visual QA, public release or credential change is claimed.

## Review-strengthened checks

The strengthened current client passed actual Player and Editor against corrected Windows archive SHA-256 `9448f1218ec74ff4abf9c5dae2a4031ce4d3c60629b68c6a851d1bef36ae9c05`, with product source still the identified 0a5cbc8 target. Installed own-MIT notice hashes and actual Player output are retained in [PR evidence](https://github.com/gua-project/gua/pull/171#issuecomment-5964365719). The client now compares all rejection-before/after semantic JSON except volatile frameSequence/revision, and requires completion.Action == Click. It saves both rejection snapshots. A macOS app slice check is also added to candidate CI; macOS execution remains unverified until that workflow runs.

### negative-rejected-state

```text
Unhandled exception. System.Exception: Missing-target rejection or no-side-effect assertion failed
   at Program.<Main>$(String[] args) in C:\Users\testk\AppData\Local\Temp\gua-unity-client-8edb0a1e46a347978e123e18d19c051b\Program.cs:line 33
   at Program.<Main>(String[] args)
```

### negative-action-type

```text
Unhandled exception. System.Exception: Correlated successful click completion missing
   at Program.<Main>$(String[] args) in C:\Users\testk\AppData\Local\Temp\gua-unity-client-8edb0a1e46a347978e123e18d19c051b\Program.cs:line 42
   at Program.<Main>(String[] args)
```

The first variant modifies only the actual post-rejection snapshot nodes[0].visible to false while keeping screen unchanged; the second modifies only the actual completion Action to Focus while keeping request/node/success fields. Both fail their intended assertions after successful build and real Player attachment. The clean source was restored with an explicit full rebuild, then actual Editor passed; no mutation is submitted. Prior record sections retain their original assertions and are not retroactively called strengthened runs.
