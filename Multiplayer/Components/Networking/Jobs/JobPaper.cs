using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.Jobs;

/// <summary>
/// The game never saves a job overview sheet: a station simply prints new ones while a player is
/// nearby and throws them away when nobody is. A sheet a player was carrying when they left comes
/// back as blank paper - the JobOverview component itself is only ever added when one is printed.
/// We write the job id into the sheet's save data ourselves, and this holds on to it until the job
/// it names arrives from the host.
/// </summary>
public class JobPaper : MonoBehaviour
{
    private static readonly List<JobPaper> all = [];

    public static IReadOnlyList<JobPaper> All => all;

    public string JobId { get; private set; }

    /// <summary>Marks a restored sheet with the job it was printed for.</summary>
    public static void Remember(GameObject paper, string jobId)
    {
        if (paper == null || string.IsNullOrEmpty(jobId))
            return;

        JobPaper marker = paper.GetComponent<JobPaper>();

        if (marker == null)
            marker = paper.AddComponent<JobPaper>();

        marker.JobId = jobId;

        JobPaperwork.PaperLoaded(marker);
    }

    private void Awake()
    {
        all.Add(this);
    }

    private void OnDestroy()
    {
        all.Remove(this);
    }
}
