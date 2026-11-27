using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using FileHub.Office.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace FileHub.Office.Services;

public static partial class OfficeDocumentService
{
    internal const long EmuPerPixel = 9525;
    internal const double SlideCanvasWidth = 960;
    internal const double SlideCanvasHeight = 540;

    // ---------------------------------------------------------------- PowerPoint: load

    private static void LoadPowerPoint(OfficeDocument doc)
    {
        using var package = PresentationDocument.Open(doc.Path, false);
        var presentationPart = package.PresentationPart
            ?? throw new InvalidOperationException("Presentation part is missing.");

        var slideIds = presentationPart.Presentation.SlideIdList?.Elements<P.SlideId>().ToList()
                       ?? new List<P.SlideId>();
        for (var i = 0; i < slideIds.Count; i++)
        {
            var relationshipId = slideIds[i].RelationshipId?.Value;
            if (relationshipId is null ||
                presentationPart.GetPartById(relationshipId) is not SlidePart slidePart) continue;

            var slide = new PowerPointSlide { Number = i + 1 };
            var shapeTree = slidePart.Slide.CommonSlideData?.ShapeTree;
            if (shapeTree is not null)
            {
                var index = 0;
                foreach (var shape in shapeTree.Elements<P.Shape>())
                    slide.Shapes.Add(ReadShape(shape, index++));
            }

            var notes = slidePart.NotesSlidePart?.NotesSlide
                ?.Descendants<A.Text>().Select(t => t.Text ?? string.Empty)
                .Where(t => !string.IsNullOrWhiteSpace(t));
            slide.Notes = notes is null ? string.Empty : string.Join("\n", notes);

            doc.Slides.Add(slide);
        }
    }

    private static PowerPointShape ReadShape(P.Shape shape, int index)
    {
        var nonVisual = shape.GetFirstChild<P.NonVisualShapeProperties>();
        var name = nonVisual?.GetFirstChild<P.NonVisualDrawingProperties>()?.Name?.Value
                   ?? $"Shape {index + 1}";
        var placeholder = nonVisual?.GetFirstChild<P.ApplicationNonVisualDrawingProperties>()
            ?.ChildElements.FirstOrDefault(e => e.LocalName == "ph");
        var placeholderXml = placeholder?.OuterXml ?? string.Empty;
        var isTitle = placeholderXml.Contains("title", StringComparison.OrdinalIgnoreCase) || index == 0;

        var transform = shape.ShapeProperties?.Transform2D;
        var offset = transform?.Offset;
        var extents = transform?.Extents;

        var result = new PowerPointShape
        {
            Name = name,
            IsTitle = isTitle,
            X = (offset?.X?.Value ?? 0) / (double)EmuPerPixel,
            Y = (offset?.Y?.Value ?? 0) / (double)EmuPerPixel,
            Width = (extents?.Cx?.Value ?? 400) / (double)EmuPerPixel,
            Height = (extents?.Cy?.Value ?? 80) / (double)EmuPerPixel
        };

        // OpenXML 3.x can surface a:txBody as an unknown element under p:sp; walk by local name.
        var textBody = shape.ChildElements.FirstOrDefault(e => e.LocalName == "txBody");
        if (textBody is not null)
        {
            var lines = textBody.ChildElements
                .Where(e => e.LocalName == "p")
                .Select(p => string.Concat(p.Descendants()
                    .Where(e => e.LocalName == "t")
                    .Select(t => t.InnerText)))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            result.Text = string.Join("\n", lines);

            var runProps = textBody.Descendants<A.RunProperties>().FirstOrDefault();
            if (runProps?.FontSize?.Value is { } size) result.FontSize = size / 100.0;
            result.IsBold = runProps?.Bold?.Value == true;
            result.ColorHex = runProps?.Descendants<A.RgbColorModelHex>().FirstOrDefault()?.Val?.Value
                              ?? string.Empty;
        }
        return result;
    }

    // ---------------------------------------------------------------- PowerPoint: save

    private static void SavePowerPoint(OfficeDocument doc)
    {
        using var package = PresentationDocument.Open(doc.Path, true);
        var presentationPart = package.PresentationPart
                               ?? throw new InvalidOperationException("Presentation part is missing.");
        var presentation = presentationPart.Presentation;
        var idList = presentation.SlideIdList ?? presentation.AppendChild(new P.SlideIdList());
        var existing = idList.Elements<P.SlideId>().ToList();
        var templatePart = existing.FirstOrDefault()?.RelationshipId?.Value is { } firstRel &&
                           presentationPart.GetPartById(firstRel) is SlidePart t ? t : null;

        var entries = new List<(P.SlideId Id, SlidePart Part)>();
        for (var i = 0; i < doc.Slides.Count; i++)
        {
            if (i < existing.Count)
            {
                var rel = existing[i].RelationshipId?.Value;
                if (rel is not null && presentationPart.GetPartById(rel) is SlidePart part)
                {
                    entries.Add((existing[i], part));
                    continue;
                }
            }

            if (templatePart is null)
                throw new InvalidOperationException("The presentation has no slide to use as a template.");
            var newPart = presentationPart.AddNewPart<SlidePart>();
            newPart.Slide = (P.Slide)templatePart.Slide.CloneNode(true);
            if (templatePart.SlideLayoutPart is not null) newPart.AddPart(templatePart.SlideLayoutPart);
            var maxId = 255U;
            foreach (var item in existing) maxId = Math.Max(maxId, item.Id?.Value ?? 0U);
            var slideId = new P.SlideId
            {
                Id = maxId + (uint)(entries.Count + 1),
                RelationshipId = presentationPart.GetIdOfPart(newPart)
            };
            entries.Add((slideId, newPart));
        }

        // Remove slides deleted in the editor.
        for (var i = doc.Slides.Count; i < existing.Count; i++)
        {
            var rel = existing[i].RelationshipId?.Value;
            if (rel is not null && presentationPart.GetPartById(rel) is SlidePart oldPart)
                presentationPart.DeletePart(oldPart);
            existing[i].Remove();
        }

        for (var i = 0; i < doc.Slides.Count; i++)
            WriteSlide(entries[i].Part, doc.Slides[i]);

        // Reorder the slide list to match the editor.
        idList.RemoveAllChildren();
        foreach (var entry in entries) idList.AppendChild(entry.Id);
        presentation.Save();
    }

    private static void WriteSlide(SlidePart part, PowerPointSlide model)
    {
        var shapeTree = part.Slide.CommonSlideData?.ShapeTree
                        ?? throw new InvalidOperationException("Slide shape tree is missing.");
        var shapes = shapeTree.Elements<P.Shape>().ToList();

        for (var i = 0; i < Math.Min(shapes.Count, model.Shapes.Count); i++)
            WriteShapeText(shapes[i], model.Shapes[i]);

        for (var i = shapes.Count; i < model.Shapes.Count; i++)
            shapeTree.AppendChild(CreateShape(model.Shapes[i], i + shapes.Count + 1));

        for (var i = model.Shapes.Count; i < shapes.Count; i++) shapes[i].Remove();

        if (!string.IsNullOrWhiteSpace(model.Notes))
            WriteNotes(part, model.Notes);
        part.Slide.Save();
    }

    private static void WriteShapeText(P.Shape shape, PowerPointShape model)
    {
        var textBody = shape.ChildElements.FirstOrDefault(e => e.LocalName == "txBody");
        if (textBody is null)
        {
            shape.AppendChild(CreateTextBody(model));
            return;
        }

        textBody.RemoveAllChildren();
        textBody.AppendChild(new A.BodyProperties());
        textBody.AppendChild(new A.ListStyle());
        foreach (var line in (model.Text ?? string.Empty).Split('\n'))
        {
            var runProps = new A.RunProperties { Language = "en-US" };
            if (model.IsBold) runProps.Bold = true;
            runProps.FontSize = (int)Math.Round(model.FontSize * 100);
            if (!string.IsNullOrWhiteSpace(model.ColorHex) &&
                model.ColorHex.Length is 6 or 8)
                runProps.AppendChild(new A.SolidFill(new A.RgbColorModelHex
                {
                    Val = model.ColorHex.Length == 8 ? model.ColorHex[2..] : model.ColorHex
                }));
            textBody.AppendChild(new A.Paragraph(new A.Run(runProps, new A.Text(line))));
        }
    }

    private static P.TextBody CreateTextBody(PowerPointShape model)
    {
        var body = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
        foreach (var line in (model.Text ?? string.Empty).Split('\n'))
            body.AppendChild(new A.Paragraph(new A.Run(new A.Text(line))));
        return body;
    }

    private static P.Shape CreateShape(PowerPointShape model, int id)
    {
        var shape = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = (uint)id, Name = model.Name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset
                    {
                        X = (long)Math.Round(model.X * EmuPerPixel),
                        Y = (long)Math.Round(model.Y * EmuPerPixel)
                    },
                    new A.Extents
                    {
                        Cx = (long)Math.Round(Math.Max(40, model.Width) * EmuPerPixel),
                        Cy = (long)Math.Round(Math.Max(30, model.Height) * EmuPerPixel)
                    }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
        shape.AppendChild(CreateTextBody(model));
        return shape;
    }

    private static void WriteNotes(SlidePart part, string notes)
    {
        var notesPart = part.NotesSlidePart ?? part.AddNewPart<NotesSlidePart>();
        var textBody = notesPart.NotesSlide?.Descendants<A.Text>().FirstOrDefault();
        if (textBody is not null) textBody.Text = notes;
    }

    // ---------------------------------------------------------------- Blank presentation

    private const string MinimalThemeXml =
        "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"FileHub\">" +
        "<a:themeElements>" +
        "<a:clrScheme name=\"FileHub\">" +
        "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1>" +
        "<a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
        "<a:dk2><a:srgbClr val=\"44546A\"/></a:dk2>" +
        "<a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2>" +
        "<a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1>" +
        "<a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2>" +
        "<a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3>" +
        "<a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>" +
        "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5>" +
        "<a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6>" +
        "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink>" +
        "<a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink>" +
        "</a:clrScheme>" +
        "<a:fontScheme name=\"FileHub\">" +
        "<a:majorFont><a:latin typeface=\"Aptos Display\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
        "<a:minorFont><a:latin typeface=\"Aptos\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont>" +
        "</a:fontScheme>" +
        "<a:fmtScheme name=\"FileHub\">" +
        "<a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst>" +
        "<a:lnStyleLst><a:ln><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst>" +
        "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
        "<a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst>" +
        "</a:fmtScheme></a:themeElements></a:theme>";

    private static void CreatePowerPoint(string path)
    {
        using var package = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = package.AddPresentationPart();
        presentationPart.Presentation = new P.Presentation(
            new P.SlideMasterIdList(new P.SlideMasterId { Id = 1U, RelationshipId = "rId1" }),
            new P.SlideIdList(),
            new P.SlideSize
            {
                Cx = (int)Math.Round(SlideCanvasWidth * EmuPerPixel),
                Cy = (int)Math.Round(SlideCanvasHeight * EmuPerPixel),
                Type = P.SlideSizeValues.Screen16x9
            });

        var masterPart = presentationPart.AddNewPart<SlideMasterPart>("rId1");
        masterPart.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(new P.ShapeTree(new P.GroupShapeProperties(new A.TransformGroup()))),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = "rId1" }),
            new P.TextStyles());

        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>("rId1");
        layoutPart.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(new P.ShapeTree(new P.GroupShapeProperties(new A.TransformGroup()))))
        {
            Type = P.SlideLayoutValues.TitleOnly
        };

        var themePart = masterPart.AddNewPart<ThemePart>("rId2");
        themePart.Theme = new A.Theme(MinimalThemeXml);

        var slidePart = presentationPart.AddNewPart<SlidePart>();
        var shapeTree = new P.ShapeTree(new P.GroupShapeProperties(new A.TransformGroup()));
        shapeTree.AppendChild(CreateShape(new PowerPointShape
        {
            Name = "Title 1", Text = "Click to add title", IsTitle = true,
            X = 60, Y = 40, Width = 840, Height = 90, FontSize = 36, IsBold = true
        }, 2));
        shapeTree.AppendChild(CreateShape(new PowerPointShape
        {
            Name = "Content 2", Text = "Click to add text",
            X = 60, Y = 150, Width = 840, Height = 330, FontSize = 20
        }, 3));
        slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree));
        slidePart.AddPart(layoutPart);
        presentationPart.Presentation.SlideIdList!.AppendChild(new P.SlideId
        {
            Id = 256U,
            RelationshipId = presentationPart.GetIdOfPart(slidePart)
        });

        presentationPart.Presentation.Save();
        masterPart.SlideMaster.Save();
        layoutPart.SlideLayout.Save();
        slidePart.Slide.Save();
    }
}
