using BOCCHI.Enums;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;

namespace BOCCHI.Data;

public class Job
{
    public readonly JobId id;

    public byte ByteId
    {
        get => (byte)id;
    }

    public readonly PlayerStatus status;

    public uint UintStatus
    {
        get => (uint)status;
    }

    private static bool? lastStateAvailable;

    /// <summary>目前的輔助職業；新月島狀態還沒載入（不在島上、讀取中）時回 null＝不知道。</summary>
    /// <remarks>GetState 只在指揮器是新月島且狀態已載入時回非 null；每次重取，不保存指標。</remarks>
    public static unsafe Job? Current
    {
        get
        {
            var state = PublicContentOccultCrescent.GetState();
            var available = state != null;
            if (lastStateAvailable != available)
            {
                lastStateAvailable = available;
                Svc.Log.Information(available
                    ? "[BOCCHI][Job] 已讀到新月島的輔助職業狀態。"
                    : "[BOCCHI][Job] 新月島的輔助職業狀態還沒載入，需要目前輔助職業的動作先略過。");
            }

            if (state == null)
            {
                return null;
            }

            var id = (JobId)state->CurrentSupportJob;
            return id switch
            {
                JobId.Freelancer => Freelancer,
                JobId.Knight => Knight,
                JobId.Berserker => Berserker,
                JobId.Monk => Monk,
                JobId.Ranger => Ranger,
                JobId.Samurai => Samurai,
                JobId.Bard => Bard,
                JobId.Geomancer => Geomancer,
                JobId.TimeMage => TimeMage,
                JobId.Cannoneer => Cannoneer,
                JobId.Chemist => Chemist,
                JobId.Oracle => Oracle,
                JobId.Thief => Thief,
                _ => Freelancer,
            };
        }
    }

    public Job(JobId id, PlayerStatus status)
    {
        this.id = id;
        this.status = status;
    }

    public void ChangeTo()
    {
        PublicContentOccultCrescent.ChangeSupportJob(ByteId);
    }

    public Chain ChangeToChain()
    {
        return Chain.Create()
            .RunIf(() => Current is { } current && current.id != id)
            .Then(_ => ChangeTo())
            .WaitUntilStatus(UintStatus);
    }

    public readonly static Job Freelancer = new(JobId.Freelancer, PlayerStatus.PhantomFreelancer);

    public readonly static Job Knight = new(JobId.Knight, PlayerStatus.PhantomKnight);

    public readonly static Job Berserker = new(JobId.Berserker, PlayerStatus.PhantomBerserker);

    public readonly static Job Monk = new(JobId.Monk, PlayerStatus.PhantomMonk);

    public readonly static Job Ranger = new(JobId.Ranger, PlayerStatus.PhantomRanger);

    public readonly static Job Samurai = new(JobId.Samurai, PlayerStatus.PhantomSamurai);

    public readonly static Job Bard = new(JobId.Bard, PlayerStatus.PhantomBard);

    public readonly static Job Geomancer = new(JobId.Geomancer, PlayerStatus.PhantomGeomancer);

    public readonly static Job TimeMage = new(JobId.TimeMage, PlayerStatus.PhantomTimeMage);

    public readonly static Job Cannoneer = new(JobId.Cannoneer, PlayerStatus.PhantomCannoneer);

    public readonly static Job Chemist = new(JobId.Chemist, PlayerStatus.PhantomChemist);

    public readonly static Job Oracle = new(JobId.Oracle, PlayerStatus.PhantomOracle);

    public readonly static Job Thief = new(JobId.Thief, PlayerStatus.PhantomThief);
}
