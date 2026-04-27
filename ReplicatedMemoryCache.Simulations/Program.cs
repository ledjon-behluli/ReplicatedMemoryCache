using ScottPlot;

int[] clusterSizes = [2, 4, 8, 16, 32, 64, 128, 256, 512, 1024];
int runsPerSize = 100; // How many times we repeat the simulation for a given cluster size before calculating the average

double[] xs = new double[clusterSizes.Length];
double[] ys = new double[clusterSizes.Length];

for (int i = 0; i < clusterSizes.Length; i++)
{
    int numNodes = clusterSizes[i];
    long totalRoundsAcc = 0;

    for (int run = 0; run < runsPerSize; run++)
    {
        // We pick a different random seed per run!
        var random = new Random(Guid.NewGuid().GetHashCode());
        totalRoundsAcc += SimulateGossipConvergence(numNodes, random);
    }

    double averageRounds = (double)totalRoundsAcc / runsPerSize;

    xs[i] = numNodes;
    ys[i] = averageRounds;
}

var plot = new Plot();
var scatter = plot.Add.Scatter(xs, ys);

scatter.LineWidth = 3;
scatter.MarkerSize = 8;

plot.Title($"Protocol convergence ({runsPerSize} runs/size)");
plot.XLabel("Number of nodes in cluster");
plot.YLabel("Average rounds to convergence");

plot.Axes.SetLimitsX(0, clusterSizes.Max() + 50);
plot.Axes.SetLimitsY(0, ys.Max() + 2);

plot.SavePng(Path.Combine(Directory.GetCurrentDirectory(), "gossip_protocol_convergence.png"), 800, 500);

static int SimulateGossipConvergence(int numNodes, Random random)
{
    bool[] current = new bool[numNodes];
    bool[] next = new bool[numNodes];

    // At round 0, a single node gets the initial write.
    current[0] = true;
    next[0] = true;

    int infectedCount = 1;
    int rounds = 0;

    while (infectedCount < numNodes)
    {
        rounds++;

        for (int i = 0; i < numNodes; i++)
        {
            int peerNode;

            do
            {
                peerNode = random.Next(numNodes);
            }
            while (peerNode == i);

            if (current[i])
            {
                next[peerNode] = true; // If I have the data, I give it to my chosen peer.
            }

            if (current[peerNode])
            {
                next[i] = true; // If my chosen peer has the data, I take it from it.
            }
        }

        infectedCount = 0;

        for (int i = 0; i < numNodes; i++)
        {
            current[i] = next[i];

            if (current[i])
            {
                infectedCount++;
            }
        }
    }

    return rounds;
}