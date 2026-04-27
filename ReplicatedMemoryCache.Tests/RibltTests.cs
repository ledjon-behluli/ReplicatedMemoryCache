using CsCheck;
using Microsoft.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Ledjon.ReplicatedMemoryCache.Tests;

public class RibltTests
{
    private static Guid IdFromNumber(ulong num) => new(MD5.HashData(Encoding.UTF8.GetBytes(num.ToString())));

    private static int RunUntilDecoded(RibltEncoder encoder, RibltDecoder decoder)
    {
        int count = 0;

        while (!decoder.IsCompleted)
        {
            decoder.ApplySymbol(encoder.ProduceNextSymbol());
            decoder.TryDecode();
            count++;
        }

        return count;
    }

    [Fact]
    public void Xor_Scalar_Matches_Simd()
    {
        IdPairGenerator()
            .Sample(x => Assert.Equal(
                MutationSymbol.SimdXor(x.Id, x.OtherId),
                MutationSymbol.ScalarXor(x.Id, x.OtherId)),
            iter: 10_000);

        static Gen<(Guid Id, Guid OtherId)> IdPairGenerator() => Gen.Select(Gen.Guid, Gen.Guid);
    }

    [Fact]
    public void RandomMapping_IsDeterministic()
    {
        var mapping1 = new RandomMapping(12345);
        var mapping2 = new RandomMapping(12345);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(mapping1.NextIndex(), mapping2.NextIndex());
        }
    }

    [Fact]
    public void CodedSymbol_Apply_HashedSymbol_IsInvolution()
    {
        var mutation = new MutationSymbol(Guid.NewGuid());
        var symbol = new HashedSymbol(mutation, mutation.GetHash());
        var coded = new CodedSymbol();

        coded.Apply(symbol, SymbolOperation.Add);
        coded.Apply(symbol, SymbolOperation.Remove);

        Assert.True(coded.IsEmpty);
        Assert.Equal(0, coded.Count);
        Assert.Equal(0UL, coded.CheckSum);
        Assert.Equal(Guid.Empty, coded.SymbolSum.Id);
    }

    [Fact]
    public void CodedSymbol_Apply_CodedSymbol_YieldsDifference()
    {
        var mutationA = new MutationSymbol(Guid.NewGuid());
        var symbolA = new HashedSymbol(mutationA, mutationA.GetHash());

        var mutationB = new MutationSymbol(Guid.NewGuid());
        var symbolB = new HashedSymbol(mutationB, mutationB.GetHash());

        var localSymbol = new CodedSymbol();
        localSymbol.Apply(symbolA, SymbolOperation.Add);

        var remoteSymbol = new CodedSymbol();
        remoteSymbol.Apply(symbolA, SymbolOperation.Add);
        remoteSymbol.Apply(symbolB, SymbolOperation.Add);

        // We subtract local symbol from remote one. 
        // symbolA should cancel out, leaving only symbolB.
        remoteSymbol.Apply(localSymbol, SymbolOperation.Remove);

        Assert.False(remoteSymbol.IsEmpty);
        Assert.Equal(1, remoteSymbol.Count);
        Assert.Equal(symbolB.Hash, remoteSymbol.CheckSum);
        Assert.Equal(symbolB.Symbol.Id, remoteSymbol.SymbolSum.Id);
    }

    [Fact]
    public void IdenticalSets_DecodeInstantly_WithOneSymbol()
    {
        var encoder = new RibltEncoder();
        var decoder = new RibltDecoder();

        for (ulong i = 1; i <= 100; i++)
        {
            var id = Guid.NewGuid();

            encoder.AddMutation(new MutationSymbol(id));
            decoder.AddMutation(new MutationSymbol(id));
        }

        var symbol = encoder.ProduceNextSymbol();

        decoder.ApplySymbol(symbol);
        decoder.TryDecode();

        Assert.True(decoder.IsCompleted);
        Assert.Empty(decoder.LocalSymbols);
        Assert.Empty(decoder.RemoteSymbols);
    }

    [Fact]
    public void ApplyDifferenceSymbol_ResolvesPureCell()
    {
        var mutation = new MutationSymbol(Guid.NewGuid());
        var hashed = new HashedSymbol(mutation, mutation.GetHash());

        // We construct a pre-computed difference cell which represents one item missing from the local set.
        var diff = new CodedSymbol();
        diff.Apply(hashed, SymbolOperation.Add);

        var decoder = new RibltDecoder();

        decoder.ApplyDifferenceSymbol(diff);
        decoder.TryDecode();

        Assert.True(decoder.IsCompleted);
        Assert.Single(decoder.RemoteSymbols);
        Assert.Equal(mutation.Id, decoder.RemoteSymbols[0].Symbol.Id);
    }

    [Fact]
    public void OneSidedDifference_SetA_HasExtra()
    {
        var encoder = new RibltEncoder();
        var decoder = new RibltDecoder();

        var ids = Enumerable.Range(1, 3).Select(x => IdFromNumber((ulong)x)).ToArray();

        // SetA has [1,2,3]
        encoder.AddMutation(new MutationSymbol(ids[0]));
        encoder.AddMutation(new MutationSymbol(ids[1]));
        encoder.AddMutation(new MutationSymbol(ids[2]));

        // SetB has [1, 2]
        decoder.AddMutation(new MutationSymbol(ids[0]));
        decoder.AddMutation(new MutationSymbol(ids[1]));

        var symbolsUsed = RunUntilDecoded(encoder, decoder);

        Assert.True(decoder.IsCompleted);
        Assert.Empty(decoder.LocalSymbols); // SetB missed nothing that he has exclusively.
        Assert.Single(decoder.RemoteSymbols);
        Assert.Equal(ids[2], decoder.RemoteSymbols[0].Symbol.Id); // SetB discovers SetA has 3
    }

    [Fact]
    public void SymmetricDifference_BothSets_HaveExtra()
    {
        var encoder = new RibltEncoder();
        var decoder = new RibltDecoder();

        var setA = Enumerable.Range(1, 10).Select(x => IdFromNumber((ulong)x)).ToArray();
        var setB = Enumerable.Range(1, 11).Where(x => x != 2).Select(x => IdFromNumber((ulong)x)).ToArray();

        foreach (var id in setA)
        {
            encoder.AddMutation(new MutationSymbol(id));
        }

        foreach (var id in setB)
        {
            decoder.AddMutation(new MutationSymbol(id));
        }

        var symbolsUsed = RunUntilDecoded(encoder, decoder);

        Assert.True(decoder.IsCompleted);
        Assert.True(decoder.RemoteSymbols.Select(x => x.Symbol.Id).ToHashSet().SetEquals([IdFromNumber(2)]));
        Assert.True(decoder.LocalSymbols.Select(x => x.Symbol.Id).ToHashSet().SetEquals([IdFromNumber(11)]));
    }

    [Fact]
    public void Encoder_Reset_ClearsStateCompletely()
    {
        var dirtyEncoder = new RibltEncoder();

        dirtyEncoder.AddMutation(new MutationSymbol(Guid.NewGuid()));
        dirtyEncoder.AddMutation(new MutationSymbol(Guid.NewGuid()));
        dirtyEncoder.AddMutation(new MutationSymbol(Guid.NewGuid()));

        // We advance the internal NextIndex and shift the min-heap.
        dirtyEncoder.ProduceNextSymbol();
        dirtyEncoder.ProduceNextSymbol();
        dirtyEncoder.ProduceNextSymbol();

        Assert.Equal(3, dirtyEncoder.NextIndex);

        dirtyEncoder.Reset();

        // We verify by comparing against a new encoder.
        // If Reset() works correctly, the dirtyEncoder should be
        // mathematically identical to a fresh encoder when fed the same data.
        var freshEncoder = new RibltEncoder();

        Assert.Equal(0, dirtyEncoder.NextIndex);

        var ids = Enumerable.Range(1, 10).Select(x => IdFromNumber((ulong)x)).ToArray();

        // Now we feed both encoders the exact same data
        foreach (var id in ids)
        {
            dirtyEncoder.AddMutation(new MutationSymbol(id));
            freshEncoder.AddMutation(new MutationSymbol(id));
        }

        // Both encoders must produce the exact same sequence!
        for (int i = 0; i < 50; i++)
        {
            var dirtySymbol = dirtyEncoder.ProduceNextSymbol();
            var freshSymbol = freshEncoder.ProduceNextSymbol();

            Assert.Equal(freshSymbol.Count, dirtySymbol.Count);
            Assert.Equal(freshSymbol.CheckSum, dirtySymbol.CheckSum);
            Assert.Equal(freshSymbol.SymbolSum.Id, dirtySymbol.SymbolSum.Id);
            Assert.Equal(freshEncoder.NextIndex, dirtyEncoder.NextIndex);
        }
    }

    [Fact]
    public void Decoder_AlwaysRecovers_SymmetricDifference()
    {
        SetGenerator().Sample(scenario =>
        {
            var enc = new RibltEncoder();
            var dec = new RibltDecoder();

            foreach (var id in scenario.SetA)
            {
                enc.AddMutation(new MutationSymbol(id));
            }

            foreach (var id in scenario.SetB)
            {
                dec.AddMutation(new MutationSymbol(id));
            }

            int symbolsUsed = RunUntilDecoded(enc, dec);

            // It must have successfully completed.
            Assert.True(dec.IsCompleted);

            // Remote must contain exactly SetA's exclusive items.
            Assert.True(dec.RemoteSymbols.Select(x => x.Symbol.Id).ToHashSet().SetEquals(scenario.SetA_Exclusive));

            // Local must contain exactly SetB's exclusive items.
            Assert.True(dec.LocalSymbols.Select(x => x.Symbol.Id).ToHashSet().SetEquals(scenario.SetB_Exclusive));
        }, iter: 1000);

        static Gen<(HashSet<Guid> SetA, HashSet<Guid> SetB, HashSet<Guid> SetA_Exclusive, HashSet<Guid> SetB_Exclusive)> SetGenerator() =>
            Gen.Select(
                Gen.ULong.Array[0, 500], // Common elements
                Gen.ULong.Array[0, 100], // SetA exclusives
                Gen.ULong.Array[0, 100]  // SetB exclusives
            ).Select(tuple =>
            {
                var common = tuple.Item1.Select(x => IdFromNumber(x)).ToHashSet();
                var setAOnly = tuple.Item2.Select(x => IdFromNumber(x)).ToHashSet();
                var setBOnly = tuple.Item3.Select(x => IdFromNumber(x)).ToHashSet();

                var setA = new HashSet<Guid>(common);
                var betB = new HashSet<Guid>(common);

                // Ensure sets are mutually exclusive so we know the exact difference
                var trueSetAOnly = new HashSet<Guid>(setAOnly.Where(x => !common.Contains(x) && !setBOnly.Contains(x)));
                var trueSetBOnly = new HashSet<Guid>(setBOnly.Where(x => !common.Contains(x) && !setAOnly.Contains(x)));

                foreach (var a in trueSetAOnly)
                {
                    setA.Add(a);
                }

                foreach (var b in trueSetBOnly)
                {
                    betB.Add(b);
                }

                return (setA, betB, trueSetAOnly, trueSetBOnly);
            });
    }

    [Fact]
    public void Encoder_IsOrderIndependent()
    {
        // The order in which symbols are added to the encoder should have no effect on the produced coded symbols.
        Gen.ULong.Array[1, 500].Sample(elements =>
        {
            var set = elements.Distinct().Select(x => IdFromNumber(x)).ToList();
            if (set.Count == 0)
            {
                return;
            }

            // We randomize order for two different encoders.
            var order1 = set.OrderBy(x => Guid.NewGuid()).ToList();
            var order2 = set.OrderBy(x => Guid.NewGuid()).ToList();

            var encoder1 = new RibltEncoder();
            var encoder2 = new RibltEncoder();

            foreach (var id in order1)
            {
                encoder1.AddMutation(new MutationSymbol(id));
            }

            foreach (var id in order2)
            {
                encoder2.AddMutation(new MutationSymbol(id));
            }

            // We generate 50 symbols with both encoders, and verify the symbols are mathematically identical.
            for (int i = 0; i < 50; i++)
            {
                var symbol1 = encoder1.ProduceNextSymbol();
                var symbol2 = encoder2.ProduceNextSymbol();

                Assert.Equal(symbol1, symbol2);
                Assert.Equal(symbol1.Count, symbol2.Count);
                Assert.Equal(symbol1.CheckSum, symbol2.CheckSum);
                Assert.Equal(symbol1.SymbolSum.Id, symbol2.SymbolSum.Id);
            }
        });
    }

    [Fact]
    public void MappingProbability_FollowsExpectedDistribution()
    {
        // The generator function C^-1(r) guarantees that all elements map to the 0th index, 
        // and probabilistically map to further indices with increasig sparsity.
        // Every single element's first random mapping must be coded symbol's 0th index.

        const long BaselineIndex = 0;

        Gen.ULong.Sample(hash =>
        {
            var mapping = new RandomMapping(hash);

            // Before calling NextIndex, LastIndex has to equal the baseline.
            Assert.Equal(BaselineIndex, mapping.LastIndex);

            // After calling NextIndex, it must be strictly monotonically increasing.
            var next1 = mapping.NextIndex();
            Assert.True(next1 > BaselineIndex);

            var next2 = mapping.NextIndex();
            Assert.True(next2 > next1);
        });
    }

    [Fact]
    public void Encoder_AddMutationAtOffset_MatchesManualFastForward()
    {
        // Adding a mutation at offset N must place the encoder into the exact same state
        // as if the mutation had been added at 0 and the encoder had then been advanced N times.
        // i.e. AddMutationAtOffset(sym, N) == AddMutation(sym); ProduceNextSymbol() x N;

        var ids = Enumerable.Range(1, 20).Select(x => IdFromNumber((ulong)x)).ToArray();

        const int Offset = 50;

        // We add all symbols at 0, then burn Offset-number of symbols to advance NextIndex.
        var manualEncoder = new RibltEncoder();
        foreach (var id in ids)
        {
            manualEncoder.AddMutation(new MutationSymbol(id));
        }

        for (int i = 0; i < Offset; i++)
        {
            manualEncoder.ProduceNextSymbol();
        }

        // Then we add all symbols directly at the requested offset.
        var forwardedEncoder = new RibltEncoder();
        foreach (var id in ids)
        {
            forwardedEncoder.AddMutationAtOffset(new MutationSymbol(id), Offset);
        }

        // The window must have been fast-forwarded to the requested offset.
        Assert.Equal(Offset, forwardedEncoder.NextIndex);
        Assert.Equal(manualEncoder.NextIndex, forwardedEncoder.NextIndex);

        // From here on, both encoders must produce identical symbol streams.
        for (int i = 0; i < 100; i++)
        {
            var manualEncoderSymbol = manualEncoder.ProduceNextSymbol();
            var forwardedEncoderSymbol = forwardedEncoder.ProduceNextSymbol();

            Assert.Equal(manualEncoder.NextIndex, forwardedEncoder.NextIndex);
            Assert.Equal(manualEncoderSymbol.Count, forwardedEncoderSymbol.Count);
            Assert.Equal(manualEncoderSymbol.CheckSum, forwardedEncoderSymbol.CheckSum);
            Assert.Equal(manualEncoderSymbol.SymbolSum.Id, forwardedEncoderSymbol.SymbolSum.Id);
        }
    }

    [Fact]
    public void Encoder_AddMutationAtOffset_Zero_BehavesLikeAddMutation()
    {
        // Offset = 0 must be a no-op fast-forward, i.e. equivalent to AddMutation.

        var ids = Enumerable.Range(1, 10).Select(x => IdFromNumber((ulong)x)).ToArray();

        var manualEncoder = new RibltEncoder();
        var forwardedEncoder = new RibltEncoder();

        foreach (var id in ids)
        {
            manualEncoder.AddMutation(new MutationSymbol(id));
            forwardedEncoder.AddMutationAtOffset(new MutationSymbol(id), 0);
        }

        Assert.Equal(0, manualEncoder.NextIndex);
        Assert.Equal(0, forwardedEncoder.NextIndex);

        for (int i = 0; i < 50; i++)
        {
            var a = manualEncoder.ProduceNextSymbol();
            var b = forwardedEncoder.ProduceNextSymbol();

            Assert.Equal(a.Count, b.Count);
            Assert.Equal(a.CheckSum, b.CheckSum);
            Assert.Equal(a.SymbolSum.Id, b.SymbolSum.Id);
        }
    }

    [Fact]
    public void Encoder_And_Decoder_AddMutationAtOffset_AreSymmetric()
    {
        // If both sides add the *same* set, at the *same* offset, and they are fed the *same* stream,
        // every symbol must cancel out to empty immediately (the "identical sets" invariant
        // must hold at any offset, not just at 0).

        var ids = Enumerable.Range(1, 50).Select(x => IdFromNumber((ulong)x)).ToArray();

        const int Offset = 1_000;

        var encoder = new RibltEncoder();
        var decoder = new RibltDecoder();

        foreach (var id in ids)
        {
            encoder.AddMutationAtOffset(new MutationSymbol(id), Offset);
            decoder.AddMutationAtOffset(new MutationSymbol(id), Offset);
        }

        // The very first symbol at index 'Offset' must already be empty after baseline subtraction.
        var symbol = encoder.ProduceNextSymbol();

        decoder.ApplySymbol(symbol);

        // With identical sets and identical offsets, the decoder should complete on the first symbol.
        Assert.True(decoder.IsCompleted);

        Assert.Empty(decoder.LocalSymbols);
        Assert.Empty(decoder.RemoteSymbols);
    }
}