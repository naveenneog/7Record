using SevenRecord.Domain.Input;
using SevenRecord.Domain.Video;

namespace SevenRecord.Analysis.Tests;

[TestClass]
public sealed class LoadingConfidencePlannerTests
{
    private static readonly LoadingSpeedEvent Candidate = new(
        "loading",
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(3),
        4,
        0.65);

    [TestMethod]
    public void AcceptsFreezeWithNoInteractionAndSilentAudio()
    {
        IReadOnlyList<LoadingSpeedEvent> result =
            LoadingConfidencePlanner.Refine(
                [Candidate],
                new CursorMetadataDocument(
                    1,
                    [
                        MoveAt(1.9, 0.5, 0.5),
                        MoveAt(3, 0.5, 0.5),
                        MoveAt(4.9, 0.5, 0.5)
                    ]),
                [
                    [
                        new AudioSilenceInterval(
                            TimeSpan.FromSeconds(1.5),
                            TimeSpan.FromSeconds(4))
                    ]
                ]);

        Assert.HasCount(1, result);
        Assert.AreEqual(0.9, result[0].Confidence);
    }

    [TestMethod]
    public void RejectsFreezeWithCursorActivity()
    {
        IReadOnlyList<LoadingSpeedEvent> result =
            LoadingConfidencePlanner.Refine(
                [Candidate],
                new CursorMetadataDocument(
                    1,
                    [
                        MoveAt(1.9, 0.5, 0.5),
                        MoveAt(3, 0.6, 0.5),
                        MoveAt(4.9, 0.6, 0.5)
                    ]),
                [
                    [
                        new AudioSilenceInterval(
                            TimeSpan.FromSeconds(1.5),
                            TimeSpan.FromSeconds(4))
                    ]
                ]);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void StationaryCursorHeartbeatIsNotInteraction()
    {
        IReadOnlyList<LoadingSpeedEvent> result =
            LoadingConfidencePlanner.Refine(
                [Candidate],
                new CursorMetadataDocument(
                    1,
                    [
                        MoveAt(1.9, 0.5, 0.5),
                        MoveAt(2, 0.5, 0.5),
                        MoveAt(3, 0.5, 0.5),
                        MoveAt(4, 0.5, 0.5),
                        MoveAt(4.9, 0.5, 0.5)
                    ]),
                [
                    [
                        new AudioSilenceInterval(
                            TimeSpan.FromSeconds(1.5),
                            TimeSpan.FromSeconds(4))
                    ]
                ]);

        Assert.HasCount(1, result);
    }

    [TestMethod]
    public void RejectsFreezeWithAudibleTrack()
    {
        IReadOnlyList<LoadingSpeedEvent> result =
            LoadingConfidencePlanner.Refine(
                [Candidate],
                new CursorMetadataDocument(
                    1,
                    [
                        MoveAt(1.9, 0.5, 0.5),
                        MoveAt(3, 0.5, 0.5),
                        MoveAt(4.9, 0.5, 0.5)
                    ]),
                [
                    [
                        new AudioSilenceInterval(
                            TimeSpan.FromSeconds(2),
                            TimeSpan.FromMilliseconds(500))
                    ]
                ]);

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void RejectsVisualOnlyEvidence()
    {
        Assert.IsEmpty(
            LoadingConfidencePlanner.Refine(
                [Candidate],
                cursor: null,
                []));
    }

    [TestMethod]
    public void RejectsCandidateOverAudioDropout()
    {
        Assert.IsEmpty(
            LoadingConfidencePlanner.Refine(
                [Candidate],
                new CursorMetadataDocument(
                    1,
                    [
                        MoveAt(1.9, 0.5, 0.5),
                        MoveAt(3, 0.5, 0.5),
                        MoveAt(4.9, 0.5, 0.5)
                    ]),
                [
                    [
                        new AudioSilenceInterval(
                            TimeSpan.FromSeconds(1.5),
                            TimeSpan.FromSeconds(4))
                    ]
                ],
                [
                    new SevenRecord.Domain.Audio.AudioGapMetadata(
                        TimeSpan.FromSeconds(3),
                        TimeSpan.FromMilliseconds(200))
                ]));
    }

    private static CursorMetadataEvent MoveAt(
        double seconds,
        double x,
        double y) =>
        new(
            TimeSpan.FromSeconds(seconds),
            10,
            10,
            x,
            y,
            CursorEventKind.Move,
            CursorButton.None);
}
