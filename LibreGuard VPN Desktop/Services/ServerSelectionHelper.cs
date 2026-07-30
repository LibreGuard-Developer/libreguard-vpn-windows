using System.Collections.Generic;
using System.Linq;
using LibreGuard_VPN_Desktop.Models;

namespace LibreGuard_VPN_Desktop.Services;

/// <summary>
/// Helper class for selecting the best VPN server based on latency, load, and user plan.
/// </summary>
internal static class ServerSelectionHelper
{
    /// <summary>
    /// Selects the best server from the provided list based on latency and load.
    /// </summary>
    /// <param name="servers">The list of available servers.</param>
    /// <param name="latencies">A dictionary mapping server hostnames to their measured latency in milliseconds.</param>
    /// <param name="isPro">Whether the user has a Pro subscription.</param>
    /// <returns>The best server, or null if no suitable server is found.</returns>
    public static ServerLocation? SelectBestServer(IEnumerable<ServerLocation> servers, Dictionary<string, int> latencies, bool isPro)
    {
        var eligibleServers = servers.Where(s => isPro || !s.IsPremium).ToList();
        
        ServerLocation? bestServer = null;
        double highestScore = -1;

        foreach (var server in eligibleServers)
        {
            if (string.IsNullOrWhiteSpace(server.ServerHostname) || 
                !latencies.TryGetValue(server.ServerHostname, out var latency) || 
                latency < 0)
            {
                continue;
            }

            double latencyWeight = 0.70;
            double loadWeight = 0.25;
            
            if (server.LoadPercent >= 70)
            {
                latencyWeight = 0.50;
                loadWeight = 0.50;
            }

            double latencyScore = 0;
            if (latency <= 50) latencyScore = 100;
            else if (latency <= 150) latencyScore = 100 - ((latency - 50) * 30.0 / 100.0);
            else if (latency <= 300) latencyScore = 70 - ((latency - 150) * 30.0 / 150.0);
            else if (latency <= 500) latencyScore = 40 - ((latency - 300) * 40.0 / 200.0);
            else latencyScore = 0;

            double loadScore = 0;
            int load = server.LoadPercent;
            if (load <= 30) loadScore = 100;
            else if (load <= 60) loadScore = 100 - ((load - 30) * 30.0 / 30.0);
            else if (load <= 80) loadScore = 70 - ((load - 60) * 40.0 / 20.0);
            else if (load <= 90) loadScore = 30 - ((load - 80) * 20.0 / 10.0);
            else loadScore = 10 - ((load - 90) * 10.0 / 10.0);

            double totalScore = (latencyScore * latencyWeight) + (loadScore * loadWeight);

            if (isPro && server.IsPremium)
            {
                totalScore += 10.0 * 0.10; // PRO_BONUS_WEIGHT
            }

            if (totalScore > highestScore)
            {
                highestScore = totalScore;
                bestServer = server;
            }
        }

        return bestServer;
    }
}
