using System;
using System.Collections.Generic;
using Multiplayer.Components.Networking;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Components;

[DisallowMultipleComponent]
public abstract class IdMonoBehaviour<T, I> : MonoBehaviour where T : struct where I : MonoBehaviour
{
    private static readonly IdPool<T> idPool = new();
    private static readonly Dictionary<T, IdMonoBehaviour<T, I>> indexToObject = [];

    private T _netId;

    public T NetId {
        get => _netId;
        set {
            if (_netId.Equals(value))
                return;

            Surrender();
            Register(value);
        }
    }

    /// <summary>
    /// Gives up the id this object currently answers to.
    ///
    /// Everything the host makes takes an id in Awake, and most of it is renamed a moment later to
    /// the id that actually belongs to it - an item the host builds on a client's word, say. Without
    /// clearing the lookup first, the old id goes back into the pool while still pointing here: the
    /// next object to be built is handed the very same id, and so is the next, and the entry ends up
    /// naming something else entirely. Whoever is later given that id then finds it already taken.
    /// </summary>
    private void Surrender()
    {
        if ((_netId as dynamic).CompareTo(default(T)) == 0)
            return;

        //Only if the id still names us - a later object may already have claimed it
        if (indexToObject.TryGetValue(_netId, out IdMonoBehaviour<T, I> current) && ReferenceEquals(current, this))
            indexToObject.Remove(_netId);

        idPool.ReleaseId(_netId);
    }

    protected abstract bool IsIdServerAuthoritative { get; }

    protected static bool Get(T netId, out IdMonoBehaviour<T, I> obj)
    {
        if (indexToObject.TryGetValue(netId, out obj))
            return true;
        obj = null;
        if ((netId as dynamic).CompareTo(default(T)) != 0)
            Multiplayer.LogDebug(() => $"Got invalid NetId {netId} for {typeof(I).Name}{(NetworkLifecycle.Instance.IsProcessingPacket ? $" while processing packet\r\n{Environment.StackTrace}" : "")}");
        return false;
    }

    protected static bool TryGet(T netId, out IdMonoBehaviour<T, I> obj)
    {
        if (indexToObject.TryGetValue(netId, out obj))
            return true;

        obj = null;
        return false;
    }

    protected virtual void Awake()
    {
        if (IsIdServerAuthoritative && !NetworkLifecycle.Instance.IsHost())
            return;
        Register(idPool.NextId);
    }

    /// <summary>
    /// Takes the next id out of the pool without anything to hang it on. The host hands these to
    /// clients for things they built themselves, and the object that answers to the id is made
    /// wherever it is actually needed.
    /// </summary>
    public static T ReserveId()
    {
        return idPool.NextId;
    }

    /// <summary>
    /// Puts a reserved id back when the thing it was meant for never turned up.
    /// </summary>
    public static void ReturnReservedId(T id)
    {
        if ((id as dynamic).CompareTo(default(T)) == 0 || indexToObject.ContainsKey(id))
            return;

        idPool.ReleaseId(id);
    }

    public void Register(T id)
    {
        _netId = id;
        indexToObject[id] = this;
    }

    protected virtual void OnDestroy()
    {
        Surrender();
        if (!UnloadWatcher.isUnloading)
            return;
        idPool.Reset();
        indexToObject.Clear();
    }
}
