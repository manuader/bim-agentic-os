# BCF 3.0 structural subset schemas

These XSDs are **project-authored**, not the official buildingSMART BCF-XML 3.0 schemas.
The official XSDs could not be fetched from this build environment (network policy), so their
licence could not be checked and nothing was vendored.

Each file is a strict *subset* of the corresponding BCF 3.0 document, written from the BCF 3.0
specification structure (no target namespace, element order as in BCF-XML 3.0):

| File | Document | Covered |
| --- | --- | --- |
| `version.xsd` | `bcf.version` | `Version/@VersionId` = `3.0` |
| `extensions.xsd` | `extensions.xml` | `TopicTypes`, `TopicStatuses`, `Priorities`, `TopicLabels`, `Users`, `SnippetTypes`, `Stages` |
| `project.xsd` | `project.bcfp` | `ProjectInfo/Project/@ProjectId`, `Name` |
| `markup.xsd` | `markup.bcf` | `Markup/Header/Files`, `Topic` attributes and the scalar topic children, `Labels`, `Viewpoints/ViewPoint` |
| `visinfo.xsd` | `*.bcfv` | `Components` (`Selection`, `Visibility`), `PerspectiveCamera`, `OrthogonalCamera` |

Known gaps against the official schemas: `BimSnippet`, `DocumentReferences`, `RelatedTopics`,
`Comments`, `ViewSetupHints`, `Coloring`, visibility `Exceptions`, `Lines`, `ClippingPlanes` and
`Bitmaps` are not modelled, so a document using them is rejected here even though BCF 3.0 allows it.
The exporter emits none of them. Replace these files with the official XSDs once they can be
fetched and their licence is confirmed permissive.
