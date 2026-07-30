using SevenRecord.Domain.Audio;

namespace SevenRecord.Domain.Tests;

[TestClass]
public sealed class AudioMixSettingsTests
{
    [TestMethod]
    public void GainIsConstrainedWithoutChangingMute()
    {
        AudioMixSettings mix =
            new AudioMixSettings(50, true).Constrain();

        Assert.AreEqual(12d, mix.GainDecibels);
        Assert.IsTrue(mix.IsMuted);
    }
}
