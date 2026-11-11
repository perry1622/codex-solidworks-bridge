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
        private void DemoPreconditions(DocEntry target)
        {
            if (target.Doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw new Fault("UNSUPPORTED_ACTION", "The demo needs an assembly document.");
            var active = app.IActiveDoc2 as IModelDoc2;
            if (!Object.ReferenceEquals(active, target.Doc))
                throw new Fault("VALIDATION_FAILED", "Target must be the currently active assembly window.");
            if (target.Doc.SketchManager.ActiveSketch != null) throw new Fault("SKETCH_ACTIVE", "Finish the current sketch first.");
            var components = ((IAssemblyDoc)target.Doc).GetComponents(false) as object[] ?? new object[0];
            if (components.Length != 0)
                throw new Fault("UNSUPPORTED_ACTION", "The first demo requires an empty assembly; existing components are preserved.");
            if (!File.Exists(options.PartTemplate)) throw new Fault("VALIDATION_FAILED", "Part template not found; configure --part-template.");
        }
        private IFeature DemoXYPlane(IModelDoc2 document)
        {
            var candidates = new List<IFeature>();
            for (var f = document.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
            {
                if (f.GetTypeName2() != "RefPlane") continue;
                var plane = f.GetSpecificFeature2() as IRefPlane;
                var t = plane == null ? null : plane.Transform.ArrayData as double[];
                if (t != null && t.Length >= 13 && Math.Abs(t[8] - 1) < 1e-8 &&
                    Math.Abs(t[2]) < 1e-8 && Math.Abs(t[5]) < 1e-8 && Math.Abs(t[6]) < 1e-8 && Math.Abs(t[7]) < 1e-8 &&
                    Math.Abs(t[9]) + Math.Abs(t[10]) + Math.Abs(t[11]) < 1e-8)
                    candidates.Add(f);
            }
            if (candidates.Count != 1) throw new Fault("REFERENCE_AMBIGUOUS", "The template must have exactly one XY plane through its origin.");
            return candidates[0];
        }
        private void DemoCircleExtrusion(IModelDoc2 document, double outerRadius, double innerRadius, double zStart, double thickness, string name)
        {
            document.ClearSelection2(true);
            if (!DemoXYPlane(document).Select2(false, 0)) throw new Fault("API_FAILED", "Cannot select XY reference plane.");
            document.SketchManager.InsertSketch(true);
            if (document.SketchManager.ActiveSketch == null) throw new Fault("API_FAILED", "Cannot enter demo sketch.");
            try
            {
                document.SketchManager.AddToDB = true;
                if (document.SketchManager.CreateCircleByRadius(0, 0, 0, outerRadius) == null)
                    throw new Fault("API_FAILED", "Cannot create outer circle.");
                if (innerRadius > 0 && document.SketchManager.CreateCircleByRadius(0, 0, 0, innerRadius) == null)
                    throw new Fault("API_FAILED", "Cannot create inner circle.");
            }
            finally { document.SketchManager.AddToDB = false; document.SketchManager.InsertSketch(true); }
            var sketch = document.Extension.GetLastFeatureAdded() as IFeature;
            document.ClearSelection2(true);
            if (sketch == null || !sketch.Select2(false, 0)) throw new Fault("API_FAILED", "Cannot select demo profile.");
            var feature = document.FeatureManager.FeatureExtrusion3(true, false, false, 0, 0, thickness, 0,
                false, false, false, false, 0, 0, false, false, false, false, true, true, true,
                Math.Abs(zStart) < 1e-10 ? 0 : 3, zStart, false) as IFeature;
            if (feature == null) throw new Fault("API_FAILED", "Demo extrusion failed: " + name);
            feature.Name = name; document.ClearSelection2(true);
            if (!document.EditRebuild3()) throw new Fault("REBUILD_FAILED", "Demo part rebuild failed: " + name);
        }
        private void DemoSave(IModelDoc2 document, string path)
        {
            if (File.Exists(path)) throw new Fault("OUTCOME_UNKNOWN", "Refusing to overwrite demo output: " + path);
            int error = 0, warning = 0;
            if (!document.Extension.SaveAs(path, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref error, ref warning) ||
                error != 0 || !File.Exists(path) || new FileInfo(path).Length == 0)
                throw new Fault("API_FAILED", "Demo save failed. warning=" + warning) { ApiMethod = "IModelDocExtension.SaveAs", ApiStatus = error };
        }
        private IFace2 DemoCylinder(IModelDoc2 part, double radius, double zWithin)
        {
            var matches = new List<IFace2>();
            foreach (var bodyRaw in ((IPartDoc)part).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? new object[0])
            foreach (var faceRaw in ((IBody2)bodyRaw).GetFaces() as object[] ?? new object[0])
            {
                var face = (IFace2)faceRaw; var surface = (ISurface)face.GetSurface();
                if (!surface.IsCylinder()) continue;
                var p = surface.CylinderParams as double[]; var b = face.GetBox() as double[];
                if (p != null && b != null && Math.Abs(p[6] - radius) < 1e-7 && Math.Abs(p[0]) + Math.Abs(p[1]) < 1e-7 &&
                    Math.Abs(Math.Abs(p[5]) - 1) < 1e-7 && b[2] <= zWithin && b[5] >= zWithin) matches.Add(face);
            }
            if (matches.Count != 1) throw new Fault("REFERENCE_AMBIGUOUS", "Cylinder radius, axis and axial region did not resolve uniquely.");
            return matches[0];
        }
        private IFace2 DemoPlane(IModelDoc2 part, double z)
        {
            var matches = new List<IFace2>();
            foreach (var bodyRaw in ((IPartDoc)part).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? new object[0])
            foreach (var faceRaw in ((IBody2)bodyRaw).GetFaces() as object[] ?? new object[0])
            {
                var face = (IFace2)faceRaw; var s = (ISurface)face.GetSurface(); var b = face.GetBox() as double[];
                if (s.IsPlane() && b != null && Math.Abs(b[2] - z) < 1e-7 && Math.Abs(b[5] - z) < 1e-7) matches.Add(face);
            }
            if (matches.Count != 1) throw new Fault("REFERENCE_AMBIGUOUS", "End face did not resolve uniquely at Z=" + z);
            return matches[0];
        }
        private void DemoMate(IModelDoc2 document, IAssemblyDoc assembly, IComponent2 a, IFace2 fa, IComponent2 b, IFace2 fb, int type, string role, JobRecord job)
        {
            document.ClearSelection2(true);
            var ea = a.GetCorrespondingEntity(fa) as IEntity; var eb = b.GetCorrespondingEntity(fb) as IEntity;
            var selection = ((ISelectionMgr)document.SelectionManager).CreateSelectData(); selection.Mark = 1;
            if (ea == null || eb == null || !ea.Select4(false, selection) || !eb.Select4(true, selection))
                throw new Fault("MATE_FAILED", "Cannot select assembly face references: " + role);
            try
            {
                int error = 0;
                var mate = assembly.AddMate5(type, (int)swMateAlign_e.swMateAlignCLOSEST, false,
                    0, 0, 0, 0, 0, 0, 0, 0, false, false, 0, out error);
                if (mate == null || error != (int)swAddMateError_e.swAddMateError_NoError)
                    throw new Fault("MATE_FAILED", "Mate creation failed: " + role) { ApiMethod = "IAssemblyDoc.AddMate5", ApiStatus = error };
                job.AddCheck(J.Check(role, "pass", "AddMate5 result and swAddMateError_e", "Mate object returned without a mate error."));
            }
            finally { document.ClearSelection2(true); }
        }
        private void DemoArtifact(string path, string format, JobRecord job)
        {
            job.AddArtifact(J.Obj("path", path, "format", format, "size_bytes", new FileInfo(path).Length,
                "sha256", J.FileHash(path), "document_id", null, "revision", null,
                "verification", J.Check("FILE_EXISTS", "pass", "native save, file length and SHA-256", "File was saved; relocation and re-open validation are separate checks.")));
        }
        private void AssembleDemo(Dictionary<string, object> args, JobRecord job)
        {
            var target = Find(J.Str(J.Map(args["action"]), "document_id"));
            string expected = Revision(target);
            string output = Paths.Within(options.OutputRoot, Path.Combine(J.Str(args, "output_dir"), job.RequestId));
            Directory.CreateDirectory(output);
            string basePath = Path.Combine(output, "01_base.SLDPRT"), shaftPath = Path.Combine(output, "02_shaft.SLDPRT");
            job.Step("create_native_base"); job.ThrowIfCancelled();
            var basePart = app.NewDocument(options.PartTemplate, 0, 0, 0) as IModelDoc2;
            if (basePart == null) throw new Fault("API_FAILED", "Cannot create base part.");
            DemoCircleExtrusion(basePart, .025, .0042, 0, .006, "BaseRing"); DemoSave(basePart, basePath); DemoArtifact(basePath, "sldprt", job);
            job.SetChanged("modified", "staged"); job.Step("create_native_shaft"); job.ThrowIfCancelled();
            var shaftPart = app.NewDocument(options.PartTemplate, 0, 0, 0) as IModelDoc2;
            if (shaftPart == null) throw new Fault("API_FAILED", "Cannot create shaft part.");
            DemoCircleExtrusion(shaftPart, .004, 0, 0, .024, "PilotShaft");
            DemoCircleExtrusion(shaftPart, .010, 0, .006, .003, "Shoulder"); DemoSave(shaftPart, shaftPath); DemoArtifact(shaftPath, "sldprt", job);
            if (Revision(target) != expected) throw new Fault("DOC_REVISION_MISMATCH", "Target assembly changed while creating parts; insertion was stopped.");
            job.Step("insert_into_requested_assembly"); job.ThrowIfCancelled();
            int activationError = 0;
            app.ActivateDoc3(target.Doc.GetTitle(), false, 0, ref activationError);
            if (activationError != 0) throw new Fault("API_FAILED", "Cannot reactivate the requested assembly.");
            var assembly = (IAssemblyDoc)target.Doc;
            var baseComponent = assembly.AddComponent5(basePath, 0, "", false, "", 0, 0, 0) as IComponent2;
            if (baseComponent == null) throw new Fault("API_FAILED", "Base insertion failed.");
            job.SetSourcePreserved(false);
            var math = (IMathUtility)app.GetMathUtility();
            var identity = (IMathTransform)math.CreateTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 });
            if (baseComponent.IsFixed())
            {
                if (!baseComponent.Select4(false, null, false)) throw new Fault("API_FAILED", "Cannot position demo base.");
                assembly.UnfixComponent(); target.Doc.ClearSelection2(true);
            }
            baseComponent.Transform2 = (MathTransform)identity;
            target.Doc.ClearSelection2(true);
            if (!baseComponent.Select4(false, null, false)) throw new Fault("API_FAILED", "Cannot select demo base.");
            assembly.FixComponent(); target.Doc.ClearSelection2(true);
            var shaftComponent = assembly.AddComponent5(shaftPath, 0, "", false, "", 0, 0, 0) as IComponent2;
            if (shaftComponent == null) throw new Fault("API_FAILED", "Shaft insertion failed.");
            if (shaftComponent.IsFixed())
            {
                if (!shaftComponent.Select4(false, null, false)) throw new Fault("API_FAILED", "Cannot select demo shaft.");
                assembly.UnfixComponent(); target.Doc.ClearSelection2(true);
            }
            shaftComponent.Transform2 = (MathTransform)identity;
            job.Step("create_concentric_and_coincident_mates");
            DemoMate(target.Doc, assembly, baseComponent, DemoCylinder(basePart, .0042, .003),
                shaftComponent, DemoCylinder(shaftPart, .004, .003), (int)swMateType_e.swMateCONCENTRIC, "SHAFT_CONCENTRIC", job);
            DemoMate(target.Doc, assembly, baseComponent, DemoPlane(basePart, .006),
                shaftComponent, DemoPlane(shaftPart, .006), (int)swMateType_e.swMateCOINCIDENT, "SHOULDER_COINCIDENT", job);
            job.Step("rebuild_and_verify_assembly");
            if (!target.Doc.EditRebuild3()) throw new Fault("REBUILD_FAILED", "Assembly rebuild failed.");
            var components = assembly.GetComponents(false) as object[] ?? new object[0];
            var transform = shaftComponent.GetTotalTransform(false).ArrayData as double[];
            if (components.Length != 2 || !baseComponent.IsFixed() || shaftComponent.IsFixed() || transform == null ||
                Math.Abs(transform[9]) + Math.Abs(transform[10]) + Math.Abs(transform[11]) > .00001 || Math.Abs(transform[8] - 1) > .00001)
                throw new Fault("MATE_FAILED", "Assembly component count, fixed state or shaft transform did not match the demo.");
            baseComponent.MaterialPropertyValues = new double[] { .12, .42, .68, .35, .75, .25, .2, 0, 0 };
            shaftComponent.MaterialPropertyValues = new double[] { .92, .47, .12, .35, .75, .25, .2, 0, 0 };
            target.Doc.ShowNamedView2("", 7); target.Doc.ViewDisplayShaded(); target.Doc.ViewZoomtofit2(); target.Doc.GraphicsRedraw2();
            string assemblyPath = Path.Combine(output, "03_demo.SLDASM"); DemoSave(target.Doc, assemblyPath); DemoArtifact(assemblyPath, "sldasm", job);
            job.AddCheck(J.Check("ASSEMBLY_READBACK", "pass", "component count, fixed state and component transform",
                "Two native components; base fixed; shaft floating with concentric and shoulder-end mates. Rotation remains free; no Motion Study was run."));
            job.SetChanged("modified", "published"); job.SetRestoration("not_needed");
        }
    }
}
