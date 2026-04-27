using CsCheck;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Ledjon.ReplicatedMemoryCache.Tests;

public class RibltSketchTests
{
    private static Guid IdFromNumber(ulong num) => new(MD5.HashData(Encoding.UTF8.GetBytes(num.ToString())));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Sketch_Ctor_ThrowsOnInvalidSize(int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RibltSketch(size));

    [Fact]
    public void Sketch_ProducesSamePrefix_AsEncoder()
    {
        // The sketch of size N must contain the same coded symbols 
        // as the first N symbols produced by the encoder, if fed the same data.

        const int SketchSize = 50;

        var sketch = new RibltSketch(SketchSize);
        var encoder = new RibltEncoder();

        var ids = Enumerable.Range(1, 100).Select(x => IdFromNumber((ulong)x)).ToArray();

        foreach (var id in ids)
        {
            var mutation = new MutationSymbol(id);

            sketch.ApplyMutation(mutation, SymbolOperation.Add);
            encoder.AddMutation(mutation);
        }

        var sketchSlice = new CodedSymbol[SketchSize];
        var readCount = sketch.ReadSlice(0, sketchSlice.AsSpan());

        Assert.Equal(SketchSize, readCount);

        for (int i = 0; i < SketchSize; i++)
        {
            var encoderSymbol = encoder.ProduceNextSymbol();
            var sketchSymbol = sketchSlice[i];

            Assert.Equal(encoderSymbol.Count, sketchSymbol.Count);
            Assert.Equal(encoderSymbol.CheckSum, sketchSymbol.CheckSum);
            Assert.Equal(encoderSymbol.SymbolSum.Id, sketchSymbol.SymbolSum.Id);
        }
    }

    [Fact]
    public void Sketch_IsCommutative()
    {
        Gen.ULong.Array[1, 500].Sample(elements =>
        {
            var set = elements.Distinct().Select(x => IdFromNumber(x)).ToList();
            if (set.Count == 0)
            {
                return;
            }

            // We create 2 randomized sequences (order by Guid) out of the same 'set'
            // so we can test that the sketch's final state depends entirely on the data we feed it,
            // not the order in which we do so i.e. it is commutative.

            var order1 = set.OrderBy(x => Guid.NewGuid()).ToList();
            var order2 = set.OrderBy(x => Guid.NewGuid()).ToList();

            const int SketchSize = 30;

            var sketch1 = new RibltSketch(SketchSize);
            var sketch2 = new RibltSketch(SketchSize);

            foreach (var id in order1)
            {
                sketch1.ApplyMutation(new MutationSymbol(id), SymbolOperation.Add);
            }

            foreach (var id in order2)
            {
                sketch2.ApplyMutation(new MutationSymbol(id), SymbolOperation.Add);
            }

            var slice1 = new CodedSymbol[SketchSize];
            var slice2 = new CodedSymbol[SketchSize];

            sketch1.ReadSlice(0, slice1.AsSpan());
            sketch2.ReadSlice(0, slice2.AsSpan());

            for (int i = 0; i < SketchSize; i++)
            {
                Assert.Equal(slice1[i].Count, slice2[i].Count);
                Assert.Equal(slice1[i].CheckSum, slice2[i].CheckSum);
                Assert.Equal(slice1[i].SymbolSum.Id, slice2[i].SymbolSum.Id);
            }
        });
    }

    [Fact]
    public void Sketch_ApplyMutation_AddThenRemoveDoesNothing()
    {
        const int SketchSize = 20;

        var sketch = new RibltSketch(SketchSize);
        var mutation = new MutationSymbol(Guid.NewGuid());

        sketch.ApplyMutation(mutation, SymbolOperation.Add);
        sketch.ApplyMutation(mutation, SymbolOperation.Remove);

        var slice = new CodedSymbol[SketchSize];
        var readCount = sketch.ReadSlice(0, slice.AsSpan());

        Assert.Equal(SketchSize, readCount);

        for (int i = 0; i < readCount; i++)
        {
            var symbol = slice[i];

            // Adding and then removing the same mutation should cancel out entirely.
            Assert.True(symbol.IsEmpty);
            Assert.Equal(0, symbol.Count);
            Assert.Equal(0UL, symbol.CheckSum);
            Assert.Equal(Guid.Empty, symbol.SymbolSum.Id);
        }
    }

    [Fact]
    public void Sketch_ReadSlice_HandlesOutOfBoundsSafely()
    {
        var sketch = new RibltSketch(10);
        var buffer = new CodedSymbol[5];

        // Reading out of bounds should return an empty list.
        Assert.Equal(0, sketch.ReadSlice(10, buffer.AsSpan()));

        // Reading partially out of bounds should clamp to the available size.
        // Offset is 8, so it should read symbols 9 and 10 (2 total)
        Assert.Equal(2, sketch.ReadSlice(8, buffer.AsSpan()));
    }

    [Fact]
    public void Sketch_ApplyMutation_IsThreadSafe()
    {
        const int SketchSize = 100;

        var sequentialSketch = new RibltSketch(SketchSize);
        var parallelSketch = new RibltSketch(SketchSize);

        var ids = Enumerable.Range(1, 5000).Select(x => IdFromNumber((ulong)x)).ToArray();

        foreach (var id in ids) // We build the skech in sequence
        {
            sequentialSketch.ApplyMutation(new MutationSymbol(id), SymbolOperation.Add);
        }

        Parallel.ForEach(ids, id => // We build the skech in parallel to hammer the LockStrip
        {
            parallelSketch.ApplyMutation(new MutationSymbol(id), SymbolOperation.Add);
        });

        var sequentialSlice = new CodedSymbol[SketchSize];
        var parallelSlice = new CodedSymbol[SketchSize];

        sequentialSketch.ReadSlice(0, sequentialSlice.AsSpan());
        parallelSketch.ReadSlice(0, parallelSlice.AsSpan());

        // The final state of the IBLT must be perfectly identical regardless of thread interleaving.
        for (int i = 0; i < SketchSize; i++)
        {
            Assert.Equal(sequentialSlice[i].Count, parallelSlice[i].Count);
            Assert.Equal(sequentialSlice[i].CheckSum, parallelSlice[i].CheckSum);
            Assert.Equal(sequentialSlice[i].SymbolSum.Id, parallelSlice[i].SymbolSum.Id);
        }
    }
}