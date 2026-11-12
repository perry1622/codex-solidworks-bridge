# Third-party files

`Bridge/SolidWorks.Interop.sldworks.dll` and `Bridge/SolidWorks.Interop.swconst.dll`
are unmodified SOLIDWORKS .NET interop assemblies, version 34.0.0.5070.
Their copyright and applicable terms remain with Dassault Systèmes SOLIDWORKS
Corporation and its licensors. The project's MIT license does not relicense them.

SOLIDWORKS documents redistributable interop assemblies in the installation's
`api/redist` directory: [official API guide](https://help.solidworks.com/2026/english/api/sldworksapiprogguide/Welcome.htm).
File checksums are recorded in `manifest.json` and `Source/provenance.json`.

The package contains the bridge and API type bindings. It does not include a
SOLIDWORKS installation, product license, Toolbox models or optional module
licenses. CAD operations require the user's licensed local installation.
