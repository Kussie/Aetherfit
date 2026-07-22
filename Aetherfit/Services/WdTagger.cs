using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Aetherfit.Services;

// A loaded WD-v3 tagger model. Not thread-safe - TagSuggestionService serializes access.
internal sealed class WdTagger : IDisposable
{
    // Tags whose underscores are the point, from SmilingWolf's own tagger code.
    private static readonly HashSet<string> Kaomojis = new(StringComparer.Ordinal)
    {
        "0_0", "(o)_(o)", "+_+", "+_-", "._.", "<o>_<o>", "<|>_<|>", "=_=", ">_<",
        "3_3", "6_9", ">_o", "@_@", "^_^", "o_o", "u_u", "x_x", "|_|", "||_||",
    };

    private readonly InferenceSession session;
    private readonly string inputName;
    private readonly string outputName;
    private readonly int inputSize;
    // Label per output index; null for categories we never suggest (character, rating).
    private readonly string?[] labels;

    private WdTagger(InferenceSession session, string?[] labels)
    {
        this.session = session;
        this.labels = labels;

        var input = session.InputMetadata.First();
        inputName = input.Key;
        outputName = session.OutputMetadata.Keys.First();
        // Shape is [batch, height, width, channels]; height carries the expected resolution (448 for v3).
        var dims = input.Value.Dimensions;
        inputSize = dims.Length == 4 && dims[1] > 0 ? dims[1] : 448;
    }

    public static WdTagger Load(string modelPath, string labelsPath)
    {
        var labels = LoadLabels(labelsPath);
        var session = new InferenceSession(modelPath);
        try
        {
            var tagger = new WdTagger(session, labels);
            var outputCount = (int)session.OutputMetadata.First().Value.Dimensions[^1];
            if (outputCount > 0 && outputCount != labels.Length)
                throw new InvalidDataException(
                    $"Model outputs {outputCount} tags but the label file lists {labels.Length}.");
            return tagger;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // selected_tags.csv: tag_id,name,category,count — category 0 = general, 4 = character, 9 = rating.
    private static string?[] LoadLabels(string path)
    {
        var result = new List<string?>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (line.Length == 0)
                continue;
            var parts = line.Split(',');
            if (parts.Length < 3)
                throw new InvalidDataException("The tag label file is malformed.");

            var name = parts[1];
            var general = parts[2] == "0";
            result.Add(general ? (Kaomojis.Contains(name) ? name : name.Replace('_', ' ')) : null);
        }

        if (result.Count == 0)
            throw new InvalidDataException("The tag label file is empty.");
        return result.ToArray();
    }

    public IReadOnlyList<(string Tag, float Score)> Tag(string imagePath, float threshold)
    {
        var tensor = Preprocess(imagePath);
        using var results = session.Run(
            new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) }, new[] { outputName });
        var scores = results[0].AsEnumerable<float>().ToArray();

        var matches = new List<(string, float)>();
        var count = Math.Min(scores.Length, labels.Length);
        for (var i = 0; i < count; i++)
        {
            // v3 exports already apply the sigmoid; a raw-logit model would leak values outside [0,1].
            var score = scores[i];
            if (score < 0f || score > 1f)
                throw new InvalidDataException("The model produced scores outside 0-1; it is not a supported WD v3 export.");
            if (labels[i] != null && score >= threshold)
                matches.Add((labels[i]!, score));
        }

        matches.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return matches;
    }

    // WD v3 input: the image centered on a white square canvas, resized to inputSize², as NHWC
    // float32 in BGR order with raw 0-255 values.
    private DenseTensor<float> Preprocess(string imagePath)
    {
        using var src = Image.Load<Rgb24>(imagePath);
        var side = Math.Max(src.Width, src.Height);

        var scale = (float)inputSize / side;
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        src.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(w, h), Sampler = KnownResamplers.Bicubic }));

        using var canvas = new Image<Rgb24>(inputSize, inputSize, Color.White.ToPixel<Rgb24>());
        canvas.Mutate(ctx => ctx.DrawImage(src, new Point((inputSize - w) / 2, (inputSize - h) / 2), 1f));

        var tensor = new DenseTensor<float>(new[] { 1, inputSize, inputSize, 3 });
        canvas.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < inputSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < inputSize; x++)
                {
                    // model wants BGR order
                    var px = row[x];
                    tensor[0, y, x, 0] = px.B;
                    tensor[0, y, x, 1] = px.G;
                    tensor[0, y, x, 2] = px.R;
                }
            }
        });

        return tensor;
    }

    public void Dispose() => session.Dispose();
}
