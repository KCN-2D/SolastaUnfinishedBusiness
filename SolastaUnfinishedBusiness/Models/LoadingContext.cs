using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

internal static class LoadingContext
{
    private const int AsyncUploadTimeSlice = 33;
    private const int AsyncUploadBufferSize = 64;

    private static readonly HashSet<Object> LoadingOwners = [];
    private static LoadingSettings _settings;
    private static bool _unloaded;

    internal static void Initialize()
    {
        Unload();
        _unloaded = false;
    }

    internal static void UpdateLoadingState(Object owner, TA.Coroutine primary, TA.Coroutine secondary = null)
    {
        if (_unloaded || !owner)
        {
            return;
        }

        if (IsLoading(primary) || IsLoading(secondary))
        {
            LoadingOwners.Add(owner);
        }
        else
        {
            LoadingOwners.Remove(owner);
        }

        ApplySettings();
    }

    internal static void Release(Object owner)
    {
        LoadingOwners.Remove(owner);
        ApplySettings();
    }

    internal static void ApplySettings()
    {
        if (LoadingOwners.Count > 0)
        {
            LoadingOwners.RemoveWhere(static owner => !owner);
        }

        if (_unloaded || LoadingOwners.Count == 0 || Main.IsApplicationQuitting ||
            !Main.Settings.EnableFastLocationLoads || !Application.isFocused)
        {
            RestoreSettings();

            return;
        }

        _settings ??= new LoadingSettings();
        _settings.Apply();
    }

    internal static void Unload()
    {
        _unloaded = true;
        LoadingOwners.Clear();
        RestoreSettings();
    }

    private static bool IsLoading(TA.Coroutine coroutine)
    {
        // TA.Coroutine does not dispose its enumerators when Reset is called and
        // catches nested exceptions itself. Its owner is the cancellation boundary.
        return coroutine != null && !coroutine.Empty && !coroutine.IsFinished && !coroutine.IsSuspended;
    }

    private static void RestoreSettings()
    {
        var settings = _settings;
        _settings = null;
        settings?.Restore();
    }

    private sealed class LoadingSettings
    {
        private ThreadPriority _originalPriority = Application.backgroundLoadingPriority;
        private int _originalTimeSlice = QualitySettings.asyncUploadTimeSlice;
        private int _originalBufferSize = QualitySettings.asyncUploadBufferSize;
        private int _originalFrameRate = Application.targetFrameRate;
        private int _originalVSyncCount = QualitySettings.vSyncCount;

        private ThreadPriority _appliedPriority = Application.backgroundLoadingPriority;
        private int _appliedTimeSlice = QualitySettings.asyncUploadTimeSlice;
        private int _appliedBufferSize = QualitySettings.asyncUploadBufferSize;
        private int _appliedFrameRate = Application.targetFrameRate;
        private int _appliedVSyncCount = QualitySettings.vSyncCount;

        internal void Apply()
        {
            // Settings initialization, focus changes and other mods can write these
            // values during a load. Keep their latest values as the restoration target.
            var priority = Application.backgroundLoadingPriority;
            var timeSlice = QualitySettings.asyncUploadTimeSlice;
            var bufferSize = QualitySettings.asyncUploadBufferSize;
            var frameRate = Application.targetFrameRate;
            var vSyncCount = QualitySettings.vSyncCount;

            if (priority != _appliedPriority)
            {
                _originalPriority = priority;
            }

            if (timeSlice != _appliedTimeSlice)
            {
                _originalTimeSlice = timeSlice;
            }

            if (bufferSize != _appliedBufferSize)
            {
                _originalBufferSize = bufferSize;
            }

            if (frameRate != _appliedFrameRate)
            {
                _originalFrameRate = frameRate;
            }

            if (vSyncCount != _appliedVSyncCount)
            {
                _originalVSyncCount = vSyncCount;
            }

            if (priority != ThreadPriority.High)
            {
                Application.backgroundLoadingPriority = ThreadPriority.High;
            }

            if (timeSlice != AsyncUploadTimeSlice)
            {
                QualitySettings.asyncUploadTimeSlice = AsyncUploadTimeSlice;
            }

            var loadingBufferSize = Mathf.Max(AsyncUploadBufferSize, _originalBufferSize);

            if (bufferSize != loadingBufferSize)
            {
                QualitySettings.asyncUploadBufferSize = loadingBufferSize;
            }

            if (frameRate != -1)
            {
                Application.targetFrameRate = -1;
            }

            if (vSyncCount != 0)
            {
                QualitySettings.vSyncCount = 0;
            }

            // Read the applied values back rather than assuming native setters leave
            // the requested values unchanged.
            _appliedPriority = Application.backgroundLoadingPriority;
            _appliedTimeSlice = QualitySettings.asyncUploadTimeSlice;
            _appliedBufferSize = QualitySettings.asyncUploadBufferSize;
            _appliedFrameRate = Application.targetFrameRate;
            _appliedVSyncCount = QualitySettings.vSyncCount;
        }

        internal void Restore()
        {
            // Restore only values still owned by this scope. A later external write
            // must survive ending the load or turning the option off.
            if (Application.backgroundLoadingPriority == _appliedPriority)
            {
                Application.backgroundLoadingPriority = _originalPriority;
            }

            if (QualitySettings.asyncUploadTimeSlice == _appliedTimeSlice)
            {
                QualitySettings.asyncUploadTimeSlice = _originalTimeSlice;
            }

            if (QualitySettings.asyncUploadBufferSize == _appliedBufferSize)
            {
                QualitySettings.asyncUploadBufferSize = _originalBufferSize;
            }

            if (Application.targetFrameRate == _appliedFrameRate)
            {
                Application.targetFrameRate = _originalFrameRate;
            }

            if (QualitySettings.vSyncCount == _appliedVSyncCount)
            {
                QualitySettings.vSyncCount = _originalVSyncCount;
            }
        }
    }
}
