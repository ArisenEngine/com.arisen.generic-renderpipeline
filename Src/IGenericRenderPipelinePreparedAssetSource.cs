using ArisenEngine.Rendering.Resources;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Rendering;

/// <summary>
/// Retains one exact prepared asset publication until the consumer releases the lease.
/// </summary>
public interface IGenericRenderPipelinePreparedAssetLease : IDisposable
{
    RuntimeAssetResidencyKey Key { get; }

    ulong DeviceGeneration { get; }

    ulong PublicationGeneration { get; }

    bool IsCurrent { get; }
}

/// <summary>
/// Retains one exact prepared mesh publication until the consumer releases the lease.
/// </summary>
public interface IGenericRenderPipelinePreparedMeshLease :
    IGenericRenderPipelinePreparedAssetLease
{
    RHIStaticMeshResource Resource { get; }
}

/// <summary>
/// Retains one exact prepared material publication until the consumer releases the lease.
/// </summary>
public interface IGenericRenderPipelinePreparedMaterialLease :
    IGenericRenderPipelinePreparedAssetLease
{
    RHIMaterialResource Resource { get; }
}

/// <summary>
/// Exact-key retained access to prepared Generic RP mesh and material publications.
/// Consumers must retain each lease through their last submitted use and release it only after
/// that submission ticket has completed. Disposing a lease never disposes a newer publication.
/// </summary>
/// <remarks>
/// The source and every lease obtained from it are Generic RP setup-thread affine. Acquisition,
/// freshness checks, and disposal must run serially on that thread; the contract is not safe for
/// concurrent use.
/// </remarks>
public interface IGenericRenderPipelinePreparedAssetSource
{
    bool TryAcquirePreparedMesh(
        in RuntimeAssetResidencyKey key,
        out IGenericRenderPipelinePreparedMeshLease lease);

    bool TryAcquirePreparedMaterial(
        in RuntimeAssetResidencyKey key,
        out IGenericRenderPipelinePreparedMaterialLease lease);
}
