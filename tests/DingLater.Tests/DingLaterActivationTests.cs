using DingLater.Core.Services;

namespace DingLater.Tests;

[TestClass]
public sealed class DingLaterActivationTests
{
    [TestMethod]
    public void InboxUri_AcceptsOnlyCanonicalUuidPath()
    {
        var expected = Guid.Parse("5d9e767c-d906-4ec0-903e-bf127a20e57f");

        Assert.IsTrue(DingLaterActivation.TryParseMessageId($"dinglater://inbox/{expected:D}", out var actual));
        Assert.AreEqual(expected, actual);

        var rejected = new[]
        {
            $"https://inbox/{expected:D}",
            $"dinglater://other/{expected:D}",
            $"dinglater://inbox/{expected:N}",
            $"dinglater://inbox/{expected:D}/",
            $"dinglater://inbox/{expected:D}?open=true",
            $"dinglater://inbox/{expected:D}#fragment",
            "dinglater://inbox/not-a-uuid"
        };

        foreach (var value in rejected)
        {
            Assert.IsFalse(DingLaterActivation.TryParseMessageId(value, out _), value);
        }
    }
}
