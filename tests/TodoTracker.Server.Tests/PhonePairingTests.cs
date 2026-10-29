using System.Net;
using Microsoft.Extensions.Time.Testing;
using TodoTracker.Server.Plugins;

namespace TodoTracker.Server.Tests;

/// <summary>Pairing codes, who may reach the phone port, and what happens when the files it keeps go wrong.</summary>
public sealed class PhonePairingTests
{
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.30");
    private static readonly IPAddress OtherLan = IPAddress.Parse("192.168.1.31");

    private static (string Token, string Name) Paired() => ("ttp_x", "Phone");

    [Fact]
    public void Wrong_guesses_slow_down_only_the_address_that_made_them()
    {
        var time = new FakeTimeProvider(ServerFixture.T0);
        var codes = new PhonePairingCodes(time);
        var code = codes.Create().Code;

        for (var i = 0; i < PhonePairingCodes.FailuresPerMinute; i++)
        {
            Assert.Equal(PhonePairingCodes.Claim.Wrong, codes.TryRedeem($"guess-{i}", Lan, Paired, out _));
        }

        // That address waits a minute, even with the right code; the owner's phone, elsewhere on the network, doesn't.
        Assert.Equal(PhonePairingCodes.Claim.TooMany, codes.TryRedeem(code, Lan, Paired, out _));
        Assert.Equal(PhonePairingCodes.Claim.Paired, codes.TryRedeem(code, OtherLan, Paired, out var token));
        Assert.Equal("ttp_x", token);

        time.Advance(TimeSpan.FromMinutes(1));
        var next = codes.Create().Code;
        Assert.Equal(PhonePairingCodes.Claim.Paired, codes.TryRedeem(next, Lan, Paired, out _));
    }

    [Fact]
    public void An_address_counted_as_ipv4_mapped_ipv6_is_the_same_address()
    {
        var codes = new PhonePairingCodes(new FakeTimeProvider(ServerFixture.T0));
        var code = codes.Create().Code;
        for (var i = 0; i < PhonePairingCodes.FailuresPerMinute; i++)
        {
            codes.TryRedeem("nope", Lan.MapToIPv6(), Paired, out _);
        }

        Assert.Equal(PhonePairingCodes.Claim.TooMany, codes.TryRedeem(code, Lan, Paired, out _));
    }

    [Fact]
    public void Many_addresses_together_hit_a_loose_limit_for_everyone()
    {
        var time = new FakeTimeProvider(ServerFixture.T0);
        var codes = new PhonePairingCodes(time);
        var code = codes.Create().Code;
        for (var i = 0; i < PhonePairingCodes.ClaimsPerMinute; i++)
        {
            codes.TryRedeem("nope", IPAddress.Parse($"10.0.{i / 250}.{(i % 250) + 1}"), Paired, out _);
        }

        Assert.Equal(PhonePairingCodes.Claim.TooMany, codes.TryRedeem(code, OtherLan, Paired, out _));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PhonePairingCodes.Claim.Paired, codes.TryRedeem(code, OtherLan, Paired, out _));
    }

    [Fact]
    public void A_code_far_too_long_is_wrong_without_being_compared()
    {
        var codes = new PhonePairingCodes(new FakeTimeProvider(ServerFixture.T0));
        var code = codes.Create().Code;

        Assert.Equal(PhonePairingCodes.Claim.Wrong, codes.TryRedeem(code + new string('x', 10_000), Lan, Paired, out _));
        Assert.Equal(PhonePairingCodes.Claim.Paired, codes.TryRedeem(code, Lan, Paired, out _));
    }

    [Fact]
    public void A_phone_that_couldnt_be_saved_keeps_the_code_for_another_try()
    {
        var codes = new PhonePairingCodes(new FakeTimeProvider(ServerFixture.T0));
        var code = codes.Create().Code;

        Assert.Equal(PhonePairingCodes.Claim.NotSaved, codes.TryRedeem(code, Lan, () => throw new IOException("disk full"), out var token));
        Assert.Null(token);
        Assert.Equal(PhonePairingCodes.Claim.Paired, codes.TryRedeem(code, Lan, Paired, out _));
    }

    [Fact]
    public async Task The_right_code_sent_twice_at_once_pairs_one_phone()
    {
        var codes = new PhonePairingCodes(new FakeTimeProvider(ServerFixture.T0));
        var code = codes.Create().Code;
        var paired = 0;

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => codes.TryRedeem(code, IPAddress.Parse($"10.1.1.{i + 1}"), () =>
        {
            Interlocked.Increment(ref paired);
            return Paired();
        }, out _))));

        Assert.Single(results, r => r == PhonePairingCodes.Claim.Paired);
        Assert.Equal(1, paired);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.2.3.4", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("192.168.0.9", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.30", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_this_network_and_tailscale_may_reach_the_phone_port(string address, bool allowed) =>
        Assert.Equal(allowed, PhoneListener.MayConnect(IPAddress.Parse(address)));

    [Fact]
    public void An_unreadable_list_of_phones_lets_no_phone_in_and_is_kept_aside()
    {
        var data = Directory.CreateTempSubdirectory("tt-phones-file-").FullName;
        try
        {
            var options = new TodoTrackerServerOptions { DataDirectory = data };
            var (token, _) = new PairedPhones(options, TimeProvider.System).Pair("iPad");
            File.WriteAllText(Path.Combine(data, "paired-phones.json"), "{ not json");

            var reloaded = new PairedPhones(options, TimeProvider.System);

            Assert.Null(reloaded.Match(token));
            Assert.Empty(reloaded.List());
            Assert.True(File.Exists(Path.Combine(data, "paired-phones.json.bad")));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public void An_unreadable_certificate_is_kept_aside_and_a_new_one_made()
    {
        var data = Directory.CreateTempSubdirectory("tt-phone-cert-bad-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(data, PhoneCertificate.FileName), "not a certificate");

            using var made = PhoneCertificate.LoadOrCreate(data);
            using var again = PhoneCertificate.LoadOrCreate(data);

            Assert.True(made.HasPrivateKey);
            Assert.Equal(PhoneCertificate.Fingerprint(made), PhoneCertificate.Fingerprint(again));
            Assert.True(File.Exists(Path.Combine(data, PhoneCertificate.FileName + ".bad")));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }
}
