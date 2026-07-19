using NetCats.Poc01.GeneratedKinds;
using NetCats.Poc08.Laws;

namespace NetCats.Pocs.Tests;

public sealed class Poc08LawTests
{
    [Fact]
    public void Reusable_kind_laws_pass_for_unary_and_partially_applied_binary_instances()
    {
        KindMonadLaws.Assert(new LatentKMonad(), kind => kind.Get<Latent<int>>().Value);
        KindMonadLaws.Assert(new EitherKMonad<string>(), kind => kind.Get<Either<string, int>>().Value);
    }

    [Fact]
    public async Task Runtime_law_suites_use_deterministic_coordination_and_virtual_time()
    {
        Assert.Empty(await LatentLaws.ValidateAsync());
        Assert.Empty(await RuntimeLaws.ValidateAsync());
    }

    [Fact]
    public void Known_broken_instance_fails_with_the_violated_law_name()
    {
        var failures = KindMonadLaws.Validate(new BrokenMonad(), kind => kind.Get<int>());

        Assert.Contains(failures, failure => failure.Law == "functor identity");
        var error = Assert.Throws<LawViolationException>(
            () => KindMonadLaws.Assert(new BrokenMonad(), kind => kind.Get<int>()));
        Assert.Contains("functor", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Race_schedules_can_be_replayed_from_the_recorded_trace()
    {
        var original = new DeterministicRaceSchedule(seed: 8675309, length: 128);
        var expected = Enumerable.Range(0, 128).Select(_ => original.Next()).ToArray();
        var replay = DeterministicRaceSchedule.Replay(original.Trace);
        var actual = Enumerable.Range(0, 128).Select(_ => replay.Next()).ToArray();

        Assert.Equal(expected, actual);
    }

    private sealed class BrokenK;

    private sealed class BrokenMonad : IMonad<BrokenK>
    {
        public K<BrokenK, A> Pure<A>(A value) => new(value!);

        public K<BrokenK, B> Map<A, B>(K<BrokenK, A> source, Func<A, B> selector) => new(default(B)!);

        public K<BrokenK, B> Bind<A, B>(K<BrokenK, A> source, Func<A, K<BrokenK, B>> selector) =>
            selector(source.Get<A>());
    }
}
