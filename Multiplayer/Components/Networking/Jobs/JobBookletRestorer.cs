using DV.Booklets;
using Multiplayer.Components.Networking.World;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.Jobs;

/// <summary>
/// A job booklet is saved with the id of the job it was printed for, and a single player game puts
/// the two back together while the save loads. On a client neither half arrives that way: the
/// booklet comes back out of that player's own storage, the job arrives from the host a moment
/// later, and nothing ever introduces them. The booklet then sits in the player's inventory reading
/// "[NO JOB]". This waits for whichever half turns up last and marries them.
/// </summary>
public static class JobBookletRestorer
{
    private const int WAIT_FRAMES = 600;
    private const float SETTLE_SECONDS = 10f;

    /// <summary>
    /// A job has just arrived from the host. Does this player already carry its booklet?
    /// </summary>
    public static void JobArrived(NetworkedJob netJob)
    {
        if (netJob == null || netJob.Job == null || !OnAClient())
            return;

        Reunite(netJob, FindWaitingBooklet(netJob.Job.ID));
    }

    /// <summary>
    /// A booklet has just come back out of storage and said which job it was printed for. If we
    /// already know that job, it can be handed back straight away.
    /// </summary>
    public static void BookletLoaded(JobBooklet booklet)
    {
        if (booklet == null || booklet.HasJobAssigned() || string.IsNullOrEmpty(booklet.jobIdLoadedData))
            return;

        if (!OnAClient())
            return;

        if (!NetworkedJob.TryGetFromJobId(booklet.jobIdLoadedData, out NetworkedJob netJob))
            return;

        //We are in the middle of the storage load, so let the booklet finish arriving first.
        NetworkLifecycle.Instance.StartCoroutine(ReuniteNextFrame(netJob, booklet));
    }

    /// <summary>
    /// The host has said everything it has to say about jobs. A booklet still holding nothing but a
    /// job id belongs to a job that no longer exists, and is only dead paper now.
    /// </summary>
    public static void JoinFinished()
    {
        if (!OnAClient())
            return;

        NetworkLifecycle.Instance.StartCoroutine(ThrowAwayBookletsWithoutAJob());
    }

    private static IEnumerator ThrowAwayBookletsWithoutAJob()
    {
        //Jobs whose cars are still being spawned arrive a little after the rest, so give them time.
        yield return new WaitForSeconds(SETTLE_SECONDS);

        foreach (JobBooklet booklet in new List<JobBooklet>(JobBooklet.allExistingJobBooklets))
        {
            if (booklet == null || booklet.HasJobAssigned() || string.IsNullOrEmpty(booklet.jobIdLoadedData))
                continue;

            Multiplayer.Log($"JobBookletRestorer: job {booklet.jobIdLoadedData} is gone, throwing its booklet away");
            booklet.DestroyJobBooklet();
        }
    }

    /// <summary>
    /// Only a client is missing the game's own booklet matching; a host loads its save the ordinary
    /// way, and a single player game has no part in any of this.
    /// </summary>
    private static bool OnAClient()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;

        return lifecycle != null && lifecycle.IsClientRunning && !lifecycle.IsHost();
    }

    private static JobBooklet FindWaitingBooklet(string jobId)
    {
        foreach (JobBooklet booklet in JobBooklet.allExistingJobBooklets)
            if (booklet != null && !booklet.HasJobAssigned() && booklet.jobIdLoadedData == jobId)
                return booklet;

        return null;
    }

    private static IEnumerator ReuniteNextFrame(NetworkedJob netJob, JobBooklet booklet)
    {
        yield return null;
        Reunite(netJob, booklet);
    }

    private static void Reunite(NetworkedJob netJob, JobBooklet booklet)
    {
        if (booklet == null || netJob == null || netJob.Job == null || booklet.HasJobAssigned())
            return;

        Multiplayer.Log($"JobBookletRestorer: the booklet this player kept belongs to job {netJob.Job.ID}, printing it again");

        try
        {
            booklet.AssignJob(netJob.Job);
            BookletCreator_Job.Render(booklet.gameObject, new Job_data(netJob.Job));
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"JobBookletRestorer: could not print job {netJob.Job.ID} into the booklet: {ex.Message}\r\n{ex.StackTrace}");
            return;
        }

        NetworkLifecycle.Instance.StartCoroutine(TellTheHost(netJob, booklet));
    }

    /// <summary>
    /// The host still believes the booklet for this job is the copy the player took with them when
    /// they left, which is no longer part of anyone's world. Point it at the one they carry now, as
    /// soon as the host has given that a name of its own.
    /// </summary>
    private static IEnumerator TellTheHost(NetworkedJob netJob, JobBooklet booklet)
    {
        for (int frame = 0; frame < WAIT_FRAMES; frame++)
        {
            if (booklet == null || netJob == null)
                yield break;

            NetworkedItem netItem = booklet.GetComponent<NetworkedItem>();

            if (netItem != null && netItem.NetId != 0)
            {
                NetworkLifecycle.Instance.Client?.SendJobBooklet(netJob.NetId, netItem.NetId);
                yield break;
            }

            yield return null;
        }

        Multiplayer.LogWarning($"JobBookletRestorer: the booklet for job {netJob?.Job?.ID} never got a name from the host");
    }
}
