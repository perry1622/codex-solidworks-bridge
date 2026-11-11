using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace IsolatedSwBridge
{
    internal sealed partial class CadAdapter
    {
        private string AssemblyComponentId(DocEntry assembly, IComponent2 component)
        {
            var bytes = assembly.Doc.Extension.GetPersistReference3(component) as byte[];
            if (bytes == null || bytes.Length == 0) throw new Fault("REFERENCE_AMBIGUOUS", "Component has no persistent reference.");
            return "component-" + J.Hash(assembly.Id + "|" + Convert.ToBase64String(bytes)).Substring(0, 32);
        }
        private List<IFace2> PartFaces(IModelDoc2 part)
        {
            var result = new List<IFace2>();
            foreach (var raw in ((IPartDoc)part).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? new object[0])
                result.AddRange((((IBody2)raw).GetFaces() as object[] ?? new object[0]).Cast<IFace2>());
            return result;
        }
        private string AssemblyFaceId(DocEntry assembly, IComponent2 component, IModelDoc2 part, IFace2 face)
        {
            var bytes = part.Extension.GetPersistReference3(face) as byte[];
            if (bytes == null || bytes.Length == 0) throw new Fault("REFERENCE_AMBIGUOUS", "Face has no persistent reference.");
            return "face-" + J.Hash(AssemblyComponentId(assembly, component) + "|" + Convert.ToBase64String(bytes)).Substring(0, 32);
        }
        private object DescribePartSurfaces(IModelDoc2 part)
        {
            return PartFaces(part).Select(face => {
                var s = (ISurface)face.GetSurface(); var b = face.GetBox() as double[];
                return J.Obj("kind", s.IsCylinder() ? "cylinder" : s.IsPlane() ? "plane" : "other",
                    "surface_parameters_si", s.IsCylinder() ? s.CylinderParams : s.IsPlane() ? s.PlaneParams : null,
                    "bbox_mm", b == null ? null : b.Select(v => v * 1000).ToArray());
            }).ToArray();
        }
        private Dictionary<string, object> DescribeAssemblyComponent(DocEntry assembly, IComponent2 component)
        {
            var transform = component.Transform2;
            var t = transform == null ? null : transform.ArrayData as double[];
            var result = J.Obj("component_id", AssemblyComponentId(assembly, component), "name", component.Name2,
                "path", component.GetPathName(), "fixed", component.IsFixed(), "suppression_status", component.GetSuppression(),
                "transform_data", t, "position_mm", t == null ? null : t.Skip(9).Take(3).Select(v => v * 1000).ToArray());
            var part = component.GetModelDoc2() as IModelDoc2;
            if (part == null || part.GetType() != 1) return result;
            var surfaces = new List<object>();
            foreach (var face in PartFaces(part).Take(500))
            {
                var surface = face.GetSurface() as ISurface;
                if (surface == null || (!surface.IsCylinder() && !surface.IsPlane())) continue;
                var values = surface.IsCylinder() ? surface.CylinderParams as double[] : surface.PlaneParams as double[];
                var box = face.GetBox() as double[];
                surfaces.Add(J.Obj("reference_id", AssemblyFaceId(assembly, component, part, face),
                    "kind", surface.IsCylinder() ? "cylinder" : "plane", "frame", "component_local",
                    "surface_parameters_si", values, "bbox_mm", box == null ? null : box.Select(v => v * 1000).ToArray()));
            }
            result["faces"] = surfaces;
            return result;
        }
        private sealed class PinTarget
        {
            internal IComponent2 Component;
            internal IModelDoc2 Part;
            internal IFace2 Hole, Seat;
            internal double[] HoleParameters;
            internal double SeatZ;
        }
        private PinTarget PinPreconditions(DocEntry target, Dictionary<string, object> action)
        {
            if (target.Doc.GetType() != 2)
                throw new Fault("VALIDATION_FAILED", "The explicitly referenced target must be an assembly.");
            if (target.Doc.SketchManager.ActiveSketch != null) throw new Fault("SKETCH_ACTIVE", "Finish the active sketch before assembly insertion.");
            string sourcePart = Paths.Absolute(J.Str(action, "source_part_path")); Paths.NoReparse(sourcePart);
            if (!File.Exists(sourcePart) || !String.Equals(Path.GetExtension(sourcePart), ".sldprt", StringComparison.OrdinalIgnoreCase))
                throw new Fault("DOC_NOT_FOUND", "An existing local native SLDPRT library part is required.");
            var components = ((IAssemblyDoc)target.Doc).GetComponents(true) as object[] ?? new object[0];
            var found = components.Cast<IComponent2>().Where(c => AssemblyComponentId(target, c) == J.Str(action, "base_component_id")).ToArray();
            if (found.Length != 1) throw new Fault("REFERENCE_AMBIGUOUS", "Inspect again to select one top-level base component.");
            var component = found[0];
            if (!component.IsFixed()) throw new Fault("UNSUPPORTED_ACTION", "This insertion requires a fixed base component to preserve existing component positions.");
            var part = component.GetModelDoc2() as IModelDoc2;
            if (part == null || part.GetType() != 1 || part.GetSaveFlag() || !File.Exists(part.GetPathName()))
                throw new Fault("UNSUPPORTED_ACTION", "The fixed base must be a resolved saved part without unsaved changes.");
            var faces = PartFaces(part);
            var holes = faces.Where(f => AssemblyFaceId(target, component, part, f) == J.Str(action, "hole_reference_id")).ToArray();
            var seats = faces.Where(f => AssemblyFaceId(target, component, part, f) == J.Str(action, "seat_reference_id")).ToArray();
            if (holes.Length != 1 || seats.Length != 1) throw new Fault("REFERENCE_AMBIGUOUS", "Hole and seat must resolve uniquely in the selected base component.");
            var cylinder = holes[0].GetSurface() as ISurface; var plane = seats[0].GetSurface() as ISurface;
            if (!cylinder.IsCylinder() || !plane.IsPlane()) throw new Fault("VALIDATION_FAILED", "Select one cylindrical hole and one planar seat.");
            var p = cylinder.CylinderParams as double[]; var hb = holes[0].GetBox() as double[]; var sb = seats[0].GetBox() as double[];
            var t = component.Transform2.ArrayData as double[];
            if (p == null || hb == null || sb == null || Math.Abs(Math.Abs(p[5]) - 1) > 1e-7 ||
                Math.Abs(p[3]) + Math.Abs(p[4]) > 1e-7 || Math.Abs(sb[5] - sb[2]) > 1e-7 || Math.Abs(sb[5] - hb[5]) > 1e-7 ||
                t == null || t.Length != 16 || Math.Abs(t[12] - 1) > 1e-8)
                throw new Fault("UNSUPPORTED_ACTION", "This pin operation supports a local-Z hole with its upper opening as the planar seat and a unit-scale component transform.");
            return new PinTarget { Component = component, Part = part, Hole = holes[0], Seat = seats[0], HoleParameters = p, SeatZ = sb[5] };
        }
        private static bool SameTransform(double[] a, double[] b)
        {
            return a != null && b != null && a.Length == b.Length && !a.Where((v, i) => Math.Abs(v - b[i]) > 1e-8).Any();
        }
        private List<IFeature> AssemblyMateFeatures(IModelDoc2 document)
        {
            var result = new List<IFeature>();
            for (var root = document.FirstFeature() as IFeature; root != null; root = root.GetNextFeature() as IFeature)
                if (root.GetTypeName2() == "MateGroup")
                    for (var feature = root.GetFirstSubFeature() as IFeature; feature != null; feature = feature.GetNextSubFeature() as IFeature)
                        if (feature.GetSpecificFeature2() is IMate2) result.Add(feature);
            return result;
        }
        private void InsertPinWithMates(Dictionary<string, object> args, JobRecord job)
        {
            var action = J.Map(args["action"]); var target = Find(J.Str(action, "document_id"));
            var selected = PinPreconditions(target, action); var assembly = (IAssemblyDoc)target.Doc;
            string expected = Revision(target), basePath = selected.Part.GetPathName(), baseHash = J.FileHash(selected.Part.GetPathName());
            var oldComponents = (assembly.GetComponents(false) as object[] ?? new object[0]).Cast<IComponent2>().ToArray();
            var oldTransforms = oldComponents.Select(c => (double[])c.Transform2.ArrayData).Select(t => (double[])t.Clone()).ToArray();
            var oldFixed = oldComponents.Select(c => c.IsFixed()).ToArray();
            string libraryPath = Paths.Absolute(J.Str(action, "source_part_path")), libraryHash = J.FileHash(Paths.Absolute(J.Str(action, "source_part_path")));
            string output = Paths.Within(options.OutputRoot, Path.Combine(J.Str(args, "output_dir"), job.RequestId));
            Directory.CreateDirectory(output); string pinPath = Path.Combine(output, "part_" + job.RequestId.Substring(0, 8) + "_" + Path.GetFileName(libraryPath));
            job.Step("copy_and_open_existing_library_part"); job.ThrowIfCancelled();
            File.Copy(libraryPath, pinPath, false); File.SetAttributes(pinPath, FileAttributes.Normal);
            int openError = 0, openWarning = 0;
            var pin = app.OpenDoc6(pinPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                action.ContainsKey("source_configuration") ? J.Str(action, "source_configuration") : "", ref openError, ref openWarning) as IModelDoc2;
            if (pin == null || openError != 0) throw new Fault("API_FAILED", "Cannot open the independent library part copy. error=" + openError);
            if (action.ContainsKey("source_configuration") && !pin.ShowConfiguration2(J.Str(action, "source_configuration")))
                throw new Fault("VALIDATION_FAILED", "The requested library configuration does not exist.");
            // Only the request-owned copy loses its Toolbox management flag; the library is untouched.
            pin.Extension.ToolboxPartType = 0;
            var pinFaces = PartFaces(pin);
            var cylinderCandidates = pinFaces.Where(f => ((ISurface)f.GetSurface()).IsCylinder()).Where(f => {
                var p = ((ISurface)f.GetSurface()).CylinderParams as double[];
                return p != null && Math.Abs(p[0]) + Math.Abs(p[1]) < 1e-7 && Math.Abs(Math.Abs(p[5]) - 1) < 1e-7 && p[6] <= selected.HoleParameters[6] + 1e-8;
            }).ToArray();
            if (cylinderCandidates.Length != 1) throw new Fault("REFERENCE_AMBIGUOUS", "Library copy must have one origin-centered local-Z shaft cylinder fitting the selected hole; found " + cylinderCandidates.Length + ". Inspect its saved configuration before another request.");
            var pinCylinder = cylinderCandidates[0]; var cylinderBox = pinCylinder.GetBox() as double[];
            var planes = pinFaces.Where(f => ((ISurface)f.GetSurface()).IsPlane()).Where(f => {
                var b = f.GetBox() as double[]; return b != null && Math.Abs(b[5] - b[2]) < 1e-7;
            }).ToArray();
            if (planes.Length == 0) throw new Fault("REFERENCE_AMBIGUOUS", "Library copy has no local-Z end plane.");
            double seatZ = planes.Min(f => ((double[])f.GetBox())[2]);
            var endPlanes = planes.Where(f => Math.Abs(((double[])f.GetBox())[2] - seatZ) < 1e-7).ToArray();
            if (endPlanes.Length != 1) throw new Fault("REFERENCE_AMBIGUOUS", "The library part lower end plane is ambiguous.");
            var pinSeat = endPlanes[0];
            double radius = ((double[])((ISurface)pinCylinder.GetSurface()).CylinderParams)[6];
            int saveError = 0, saveWarning = 0;
            if (!pin.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveError, ref saveWarning) || saveError != 0)
                throw new Fault("API_FAILED", "Cannot save the independent library copy.");
            DemoArtifact(pinPath, "sldprt", job); job.SetChanged("modified", "staged");
            if (Revision(target) != expected || J.FileHash(basePath) != baseHash) throw new Fault("DOC_REVISION_MISMATCH", "Assembly or base changed while creating the new part; insertion stopped.");
            job.ThrowIfCancelled(); job.Step("insert_into_existing_assembly");
            int error = 0; app.ActivateDoc3(target.Doc.GetTitle(), false, 0, ref error);
            if (error != 0) throw new Fault("API_FAILED", "Cannot activate the requested assembly.");
            var baseTransform = selected.Component.Transform2.ArrayData as double[];
            double x = selected.HoleParameters[0], y = selected.HoleParameters[1], z = selected.SeatZ + seatZ;
            double px = x * baseTransform[0] + y * baseTransform[3] + z * baseTransform[6] + baseTransform[9];
            double py = x * baseTransform[1] + y * baseTransform[4] + z * baseTransform[7] + baseTransform[10];
            double pz = x * baseTransform[2] + y * baseTransform[5] + z * baseTransform[8] + baseTransform[11];
            var placement = new double[] { baseTransform[0], baseTransform[1], baseTransform[2], -baseTransform[3], -baseTransform[4], -baseTransform[5],
                -baseTransform[6], -baseTransform[7], -baseTransform[8], px, py, pz, 1, 0, 0, 0 };
            var inserted = assembly.AddComponent5(pinPath, 0, "", false, "", px, py, pz) as IComponent2;
            if (inserted == null) throw new Fault("API_FAILED", "New component insertion failed.");
            job.SetSourcePreserved(false);
            if (inserted.IsFixed())
            {
                if (!inserted.Select4(false, null, false)) throw new Fault("API_FAILED", "Cannot select the new pin for floating placement.");
                assembly.UnfixComponent(); target.Doc.ClearSelection2(true);
            }
            inserted.Transform2 = (MathTransform)((IMathUtility)app.GetMathUtility()).CreateTransform(placement);
            job.Step("create_hole_and_seat_mates");
            DemoMate(target.Doc, assembly, selected.Component, selected.Hole, inserted,
                pinCylinder, (int)swMateType_e.swMateCONCENTRIC, "PIN_CONCENTRIC", job);
            DemoMate(target.Doc, assembly, selected.Component, selected.Seat, inserted,
                pinSeat, (int)swMateType_e.swMateCOINCIDENT, "PIN_SEATED", job);
            job.Step("verify_existing_and_inserted_components");
            if (!target.Doc.EditRebuild3()) throw new Fault("REBUILD_FAILED", "The adjusted assembly did not rebuild successfully.");
            var now = assembly.GetComponents(false) as object[] ?? new object[0];
            if (now.Length != oldComponents.Length + 1 || inserted.IsFixed()) throw new Fault("MATE_FAILED", "Unexpected component count or inserted pin fixed state.");
            for (int i = 0; i < oldComponents.Length; i++)
                if (oldFixed[i] != oldComponents[i].IsFixed() || !SameTransform(oldTransforms[i], oldComponents[i].Transform2.ArrayData as double[]))
                    throw new Fault("MATE_FAILED", "An existing component moved or changed fixed state.");
            var actual = inserted.Transform2.ArrayData as double[];
            if (actual == null || new[] { 6, 7, 8, 9, 10, 11 }.Any(i => Math.Abs(actual[i] - placement[i]) > 1e-7))
                throw new Fault("MATE_FAILED", "Pin axis or shoulder position differs from the intended seat.");
            var mates = (inserted.GetMates() as object[] ?? new object[0]).Cast<IMate2>().ToArray();
            if (!mates.Any(m => m.Type == (int)swMateType_e.swMateCONCENTRIC) || !mates.Any(m => m.Type == (int)swMateType_e.swMateCOINCIDENT))
                throw new Fault("MATE_FAILED", "Both mate types were not found on the inserted component.");
            foreach (var feature in AssemblyMateFeatures(target.Doc))
            {
                bool warning;
                if (feature.GetErrorCode2(out warning) != 0) throw new Fault("MATE_FAILED", "An assembly mate reports a feature error.");
            }
            inserted.MaterialPropertyValues = new double[] { .92, .47, .12, .35, .75, .25, .2, 0, 0 };
            target.Doc.ShowNamedView2("", 7); target.Doc.ViewDisplayShaded(); target.Doc.ViewZoomtofit2(); target.Doc.GraphicsRedraw2();
            job.Step("save_new_assembly_file");
            string assemblyPath = Path.Combine(output, "02_assembly_with_pin.SLDASM");
            DemoSave(target.Doc, assemblyPath); DemoArtifact(assemblyPath, "sldasm", job);
            if (J.FileHash(basePath) != baseHash || J.FileHash(libraryPath) != libraryHash) throw new Fault("OUTCOME_UNKNOWN", "Existing part file changed during the operation.");
            var check = J.Check("INSERTION_READBACK", "pass", "component count, transforms, fixed state, mate types, mate feature errors, base SHA-256",
                "One existing library part inserted with concentric and coincident mates; original components and base part file preserved. Axial rotation remains free; no Motion Study or interference analysis was run.");
            check["measurements"] = J.Obj("components_before", oldComponents.Length, "components_after", now.Length, "pin_mates", mates.Length,
                "pin_x_mm", actual[9] * 1000, "pin_y_mm", actual[10] * 1000, "pin_z_mm", actual[11] * 1000, "existing_part_unchanged", true, "library_file_unchanged", true, "library_configuration", Configuration(pin), "pin_diameter_mm", radius * 2000, "hole_diameter_mm", selected.HoleParameters[6] * 2000);
            job.AddCheck(check); job.SetChanged("modified", "published"); job.SetRestoration("not_needed");
        }
    }
}
