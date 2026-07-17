using System.Speech.Synthesis;
using System.Text.Json;
using SevenRecord.Domain.Captions;
using SevenRecord.Transcription;

string outputRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(
        Path.GetTempPath(),
        "SevenRecord.Transcription.Probe",
        Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(outputRoot);
string speechPath = Path.Combine(outputRoot, "speech.wav");
string modelPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "7Record",
    "Models",
    "ggml-tiny.bin");

using (SpeechSynthesizer synthesizer = new())
{
    synthesizer.SetOutputToWaveFile(speechPath);
    synthesizer.Speak(
        "This is a Seven Record local caption test for clear software tutorials.");
}

CaptionDocument captions = await LocalWhisperTranscriber.TranscribeAsync(
    speechPath,
    modelPath,
    "en");
string captionJsonPath = Path.Combine(outputRoot, "captions.json");
string srtPath = Path.Combine(outputRoot, "captions.srt");
string vttPath = Path.Combine(outputRoot, "captions.vtt");
await File.WriteAllTextAsync(
    captionJsonPath,
    JsonSerializer.Serialize(
        captions,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
await File.WriteAllTextAsync(srtPath, CaptionFormatter.ToSrt(captions));
await File.WriteAllTextAsync(vttPath, CaptionFormatter.ToVtt(captions));

Console.WriteLine(JsonSerializer.Serialize(
    new
    {
        outputRoot,
        modelPath,
        segments = captions.Segments.Count,
        text = string.Join(" ", captions.Segments.Select(segment => segment.Text)),
        captionJsonPath,
        srtPath,
        vttPath,
    },
    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
