using DV.Booklets;
using DV.Booklets.Rendered;
using DV.CabControls;
using DV.InventorySystem;
using DV.Logic.Job;
using DV.RenderTextureSystem;
using DV.RenderTextureSystem.BookletRender;
using DV.Utils;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data.Jobs;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking.Jobs;

/// <summary>
/// Job paperwork is saved with the id of the job it was printed for, and the game puts the two back
/// together while a save loads - but only for booklets, and only from the storage of whoever is
/// loading the save.
///
/// That leaves two gaps in multiplayer. A client's paperwork comes back out of its own storage while
/// the job arrives from the host a moment later, and nothing introduces them, so a booklet reads
/// "[NO JOB]". And an overview sheet is never matched for anybody, not even in single player, because
/// the game does not expect one to be saved at all.
///
/// This waits for whichever half turns up last and marries them, and throws away paperwork whose job
/// is gone for good.
/// </summary>
public static class JobPaperwork
{
    private const int WAIT_FRAMES = 600;
    private const float SETTLE_SECONDS = 10f;

    #region The two halves finding each other

    /// <summary>
    /// A job has just arrived from the host. Returns whether this player already carries its
    /// overview sheet, so the station knows not to print a second copy of the same one.
    /// </summary>
    public static bool JobArrived(NetworkedJob netJob)
    {
        if (netJob == null || netJob.Job == null || !OnAClient())
            return false;

        Reunite(netJob, FindWaitingBooklet(netJob.Job.ID));

        return Reunite(netJob, FindWaitingPaper(netJob.Job.ID));
    }

    /// <summary>
    /// A job has just been loaded from the host's own save. Its booklet is the game's business, but
    /// nobody else will ever claim its overview sheet.
    /// </summary>
    public static void HostJobLoaded(NetworkedJob netJob)
    {
        if (netJob == null || netJob.Job == null || NetworkLifecycle.Instance?.IsHost() != true)
            return;

        Reunite(netJob, FindWaitingPaper(netJob.Job.ID));
    }

    /// <summary>
    /// A booklet has just come back out of storage and said which job it was printed for.
    /// </summary>
    public static void BookletLoaded(JobBooklet booklet)
    {
        if (booklet == null || booklet.HasJobAssigned() || string.IsNullOrEmpty(booklet.jobIdLoadedData))
            return;

        if (!OnAClient() || !NetworkedJob.TryGetFromJobId(booklet.jobIdLoadedData, out NetworkedJob netJob))
            return;

        //We are in the middle of the storage load, so let the item finish arriving first.
        NetworkLifecycle.Instance.StartCoroutine(ReuniteBookletNextFrame(netJob, booklet));
    }

    /// <summary>
    /// A sheet has just come back out of storage and said which job it was printed for.
    /// </summary>
    public static void PaperLoaded(JobPaper paper)
    {
        if (paper == null || string.IsNullOrEmpty(paper.JobId) || !InASession())
            return;

        if (!NetworkedJob.TryGetFromJobId(paper.JobId, out NetworkedJob netJob))
            return;

        NetworkLifecycle.Instance.StartCoroutine(ReunitePaperNextFrame(netJob, paper));
    }

    private static IEnumerator ReuniteBookletNextFrame(NetworkedJob netJob, JobBooklet booklet)
    {
        yield return null;
        Reunite(netJob, booklet);
    }

    private static IEnumerator ReunitePaperNextFrame(NetworkedJob netJob, JobPaper paper)
    {
        yield return null;
        Reunite(netJob, paper);
    }

    private static JobBooklet FindWaitingBooklet(string jobId)
    {
        foreach (JobBooklet booklet in JobBooklet.allExistingJobBooklets)
            if (booklet != null && !booklet.HasJobAssigned() && booklet.jobIdLoadedData == jobId)
                return booklet;

        return null;
    }

    private static JobPaper FindWaitingPaper(string jobId)
    {
        foreach (JobPaper paper in JobPaper.All)
            if (paper != null && paper.JobId == jobId && paper.GetComponent<JobOverview>() == null)
                return paper;

        return null;
    }

    #endregion

    #region Putting the job back into the paper

    private static bool Reunite(NetworkedJob netJob, JobBooklet booklet)
    {
        if (booklet == null || netJob == null || netJob.Job == null || booklet.HasJobAssigned())
            return false;

        Multiplayer.Log($"JobPaperwork: the booklet this player kept belongs to job {netJob.Job.ID}, printing it again");

        if (!PrintBooklet(booklet, netJob.Job))
            return false;

        NetworkLifecycle.Instance.StartCoroutine(LinkToJob(netJob, booklet.gameObject, ValidationType.JobBooklet));
        return true;
    }

    private static bool Reunite(NetworkedJob netJob, JobPaper paper)
    {
        if (paper == null || netJob == null || netJob.Job == null || paper.GetComponent<JobOverview>() != null)
            return false;

        Multiplayer.Log($"JobPaperwork: the sheet this player kept belongs to job {netJob.Job.ID}, printing it again");

        if (PrintOverview(paper.gameObject, netJob.Job) == null)
            return false;

        NetworkLifecycle.Instance.StartCoroutine(LinkToJob(netJob, paper.gameObject, ValidationType.JobOverview));
        return true;
    }

    /// <summary>
    /// Gives a blank booklet its job back and prints the pages again.
    /// </summary>
    public static bool PrintBooklet(JobBooklet booklet, Job job)
    {
        if (booklet == null || job == null || booklet.HasJobAssigned())
            return false;

        try
        {
            booklet.AssignJob(job);
            BookletCreator_Job.Render(booklet.gameObject, new Job_data(job));
            return true;
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"JobPaperwork: could not print job {job.ID} into a booklet: {ex.Message}\r\n{ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// Turns a blank sheet back into the overview for a job. The game only ever does this while
    /// instantiating a brand new sheet, so the printing half of BookletCreator_JobOverview is
    /// repeated here against the sheet the player already carries.
    /// </summary>
    public static JobOverview PrintOverview(GameObject paper, Job job)
    {
        if (paper == null || job == null)
            return null;

        JobOverview overview = paper.GetComponent<JobOverview>();

        if (overview != null)
            return overview;

        try
        {
            List<TemplatePaperData> pages = BookletCreator_JobOverview.GetJobOverviewTemplateData(new Job_data(job));

            if (pages == null)
            {
                Multiplayer.LogWarning($"JobPaperwork: no overview pages for job {job.ID}, leaving the sheet blank");
                return null;
            }

            RenderedTexturesBase textures = paper.GetComponent<RenderedTexturesBase>();

            if (textures == null)
            {
                Multiplayer.LogWarning($"JobPaperwork: {paper.name} is not a printable sheet");
                return null;
            }

            paper.name = $"JobOverview[{job.ID}]";

            JobOverviewRender render = ((GameObject)UnityEngine.Object.Instantiate(
                Resources.Load("JobOverviewRender", typeof(GameObject)),
                SingletonBehaviour<RenderTextureSystem>.Instance.transform.position,
                Quaternion.identity)).GetComponent<JobOverviewRender>();

            textures.RegisterTexturesGeneratedEvent(render);
            render.GenerateTextures(pages.ToArray());

            overview = paper.AddComponent<JobOverview>();
            overview.job = job;

            return overview;
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"JobPaperwork: could not print job {job.ID} onto a sheet: {ex.Message}\r\n{ex.StackTrace}");
            return null;
        }
    }

    #endregion

    #region Handing the paperwork to the job

    /// <summary>
    /// Whoever holds the job has to be told which item its paperwork is now: the copy they knew
    /// about left with the player, and is no longer part of anyone's world. On a client that means
    /// telling the host, once the host has given the item a name of its own.
    /// </summary>
    private static IEnumerator LinkToJob(NetworkedJob netJob, GameObject paperwork, ValidationType kind)
    {
        bool host = NetworkLifecycle.Instance.IsHost();

        for (int frame = 0; frame < WAIT_FRAMES; frame++)
        {
            if (paperwork == null || netJob == null)
                yield break;

            NetworkedItem netItem = paperwork.GetComponent<NetworkedItem>();

            if (netItem != null && (host || netItem.NetId != 0))
            {
                Adopt(netItem, kind);

                if (host)
                {
                    if (kind == ValidationType.JobBooklet)
                        netJob.JobBooklet = netItem;
                    else
                        netJob.JobOverview = netItem;
                }
                else
                {
                    NetworkLifecycle.Instance.Client?.SendJobPaperwork(netJob.NetId, netItem.NetId, kind);
                }

                yield break;
            }

            yield return null;
        }

        Multiplayer.LogWarning($"JobPaperwork: the {kind} for job {netJob?.Job?.ID} never became an item of its own");
    }

    /// <summary>
    /// An item restored from storage is tracked as plain paper: nothing ever told it what it grew
    /// into. Until it is, a job will not take it as its paperwork.
    /// </summary>
    public static void Adopt(NetworkedItem netItem, ValidationType kind)
    {
        if (netItem == null)
            return;

        if (kind == ValidationType.JobBooklet)
        {
            JobBooklet booklet = netItem.GetComponent<JobBooklet>();

            if (booklet != null)
                netItem.Initialize(booklet, netItem.NetId, false);

            return;
        }

        JobOverview overview = netItem.GetComponent<JobOverview>();

        if (overview != null)
            netItem.Initialize(overview, netItem.NetId, false);
    }

    #endregion

    #region Paperwork nobody wants any more

    /// <summary>
    /// The host has said everything it has to say about jobs. Paperwork still holding nothing but a
    /// job id belongs to a job that no longer exists, and is only dead paper now.
    /// </summary>
    public static void JoinFinished()
    {
        if (!OnAClient())
            return;

        NetworkLifecycle.Instance.StartCoroutine(ThrowAwayPaperworkWithoutAJob(true));
    }

    /// <summary>
    /// The host's own save is loaded. The game has already dealt with booklets nobody claimed, but
    /// nothing has ever looked at sheets.
    /// </summary>
    public static void SaveLoaded()
    {
        if (NetworkLifecycle.Instance?.IsHost() != true)
            return;

        NetworkLifecycle.Instance.StartCoroutine(ThrowAwayPaperworkWithoutAJob(false));
    }

    private static IEnumerator ThrowAwayPaperworkWithoutAJob(bool includeBooklets)
    {
        //Jobs whose cars are still being spawned arrive a little after the rest, so give them time.
        yield return new WaitForSeconds(SETTLE_SECONDS);

        if (includeBooklets)
        {
            foreach (JobBooklet booklet in new List<JobBooklet>(JobBooklet.allExistingJobBooklets))
            {
                if (booklet == null || booklet.HasJobAssigned() || string.IsNullOrEmpty(booklet.jobIdLoadedData))
                    continue;

                Multiplayer.Log($"JobPaperwork: job {booklet.jobIdLoadedData} is gone, throwing its booklet away");
                booklet.DestroyJobBooklet();
            }
        }

        foreach (JobPaper paper in new List<JobPaper>(JobPaper.All))
        {
            if (paper == null || paper.GetComponent<JobOverview>() != null)
                continue;

            Multiplayer.Log($"JobPaperwork: job {paper.JobId} is gone, throwing its sheet away");

            ItemBase item = TakeOutOfKeeping(paper.gameObject);

            if (item != null && item.IsGrabbed())
            {
                item.ForceEndInteraction();
                yield return null;
            }

            if (paper != null)
                UnityEngine.Object.Destroy(paper.gameObject);
        }
    }

    /// <summary>
    /// Takes a sheet out of wherever the player is keeping it. Destroying the object without doing
    /// that first leaves the inventory holding a hole.
    /// </summary>
    private static ItemBase TakeOutOfKeeping(GameObject paper)
    {
        ItemBase item = paper.GetComponent<ItemBase>();

        if (item == null)
            return null;

        int slot = Inventory.Instance != null ? Inventory.Instance.IndexOf(paper) : -1;

        if (slot >= 0)
            Inventory.Instance.DropItemFromHandsOrInventory(slot, false);
        else if (StorageController.Instance != null)
            StorageController.Instance.RemoveItemFromStorageItemList(item);

        return item;
    }

    #endregion

    /// <summary>
    /// Only a client is missing the game's own booklet matching; a host loads its save the ordinary
    /// way, and a single player game has no part in that half of this.
    /// </summary>
    private static bool OnAClient()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;

        return lifecycle != null && lifecycle.IsClientRunning && !lifecycle.IsHost();
    }

    /// <summary>
    /// Overview sheets are nobody's business but ours, host and client alike.
    /// </summary>
    private static bool InASession()
    {
        NetworkLifecycle lifecycle = NetworkLifecycle.Instance;

        return lifecycle != null && (lifecycle.IsClientRunning || lifecycle.IsHost());
    }
}
