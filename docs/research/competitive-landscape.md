# Initial research

Autodesk [Revit 2025 .NET 8 migration](https://help.autodesk.com/cloudhelp/2025/CHS/Revit-API/files/Revit_API_Developers_Guide/Introduction/Getting_Started/Using_the_Autodesk_Revit_API/Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Using_the_Autodesk_Revit_API_NET8_Update_html.html) identifies .NET 8 as the build target. Local DLL is 25.2, so this implementation targets that release rather than claiming 2027 compatibility.

Autodesk [transactions](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Transactions_html.html) require supported API contexts. [External events](https://blog.autodesk.io/document-session-id-api-context-and-external-events/) provide a valid dispatcher context. Build the small native kernel; integrate external geometry engines only after license and fidelity assessment.

SWAPP, Pirros, Glyph, Finch, TestFit, Rhino, IfcOpenShell and other benchmarks in the master prompt have NOT been audited in this session. No competitive absence or superiority claim is made. Full comparative census is pending.
