using System;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Api.Helpers;
internal static class UiHelpers
{
    [ThreadStatic]
    private static NativePanelInitialization _nativePanelInitialization;

    internal static bool NativePanelShowInitializationAvailable { get; set; }

    internal static NativePanelInitialization BeginNativePanelInitialization(
        GuiPanel owner,
        GuiPanel panel,
        Func<bool> isCurrent)
    {
        return owner && panel ? new NativePanelInitialization(owner, panel, isCurrent) : null;
    }

    internal static void RestorePreparedPanelActive(GameObject target, bool active)
    {
        if (_nativePanelInitialization?.TryKeepPreparedActive(target, active) != true)
        {
            target.SetActive(active);
        }
    }

    internal static bool WasShownBeforePanelInitialization(GuiPanel panel)
    {
        var scope = _nativePanelInitialization;
        return scope != null && scope.TryGetPriorShown(panel, out var shown) ? shown : panel.Shown;
    }

    internal static void ObserveNativePanelBeginShow(GuiPanel panel)
    {
        _nativePanelInitialization?.ObserveBeginShow(panel);
    }

    // A native owner temporarily activates a hidden child for Bind/layout, then
    // immediately calls Show. Keep that prepared hierarchy active while preserving
    // the original Show guard and its virtual OnBeginShow callback.
    internal sealed class NativePanelInitialization
    {
        private readonly NativePanelInitialization _previous;
        private readonly GuiPanel _owner;
        private readonly GuiPanel _panel;
        private readonly Func<bool> _isCurrent;
        private readonly bool _priorActive;
        private readonly bool _priorShown;
        private readonly bool _eligible;
        private bool _keptPreparedActive;
        private bool _finished;
        private int _shownGuardVisits;
        private int _nativeBeginShowCalls;

        internal NativePanelInitialization(GuiPanel owner, GuiPanel panel, Func<bool> isCurrent)
        {
            _owner = owner;
            _panel = panel;
            _isCurrent = isCurrent;
            _priorActive = panel.gameObject.activeSelf;
            _priorShown = panel.Shown;
            _eligible = NativePanelShowInitializationAvailable && IsCurrent() &&
                        !_priorActive && !panel.Showing && !panel.Hiding;
            _previous = _nativePanelInitialization;
            _nativePanelInitialization = this;
        }

        private bool IsCurrent() => _owner && _panel &&
            (ReferenceEquals(_owner, _panel) || _owner.Visible) && !_owner.Hiding && _isCurrent();

        internal bool TryKeepPreparedActive(GameObject target, bool active)
        {
            if (active || !_eligible || !IsCurrent() || target != _panel.gameObject || !target.activeSelf)
            {
                return false;
            }

            _keptPreparedActive = true;
            return true;
        }

        internal bool TryGetPriorShown(GuiPanel panel, out bool shown)
        {
            shown = _priorShown;
            if (!_eligible || !_keptPreparedActive || _shownGuardVisits != 0 ||
                !ReferenceEquals(panel, _panel) || !IsCurrent())
            {
                return false;
            }

            _shownGuardVisits++;
            return true;
        }

        internal void ObserveBeginShow(GuiPanel panel)
        {
            if (ReferenceEquals(panel, _panel))
            {
                _nativeBeginShowCalls++;
            }
        }

        internal Exception Finish(Exception originalException)
        {
            if (_finished)
            {
                return originalException;
            }

            _finished = true;
            if (!ReferenceEquals(_nativePanelInitialization, this))
            {
                return RestorePreparedActive(Combine(originalException,
                    new InvalidOperationException("Native panel initialization scope mismatch.")));
            }

            _nativePanelInitialization = _previous;
            while (_nativePanelInitialization is { _finished: true })
            {
                _nativePanelInitialization = _nativePanelInitialization._previous;
            }
            var committed = _keptPreparedActive && originalException == null && _shownGuardVisits == 1 &&
                            _nativeBeginShowCalls == 1 && IsCurrent() && _panel.Visible;
            if (!_keptPreparedActive || committed || !_panel)
            {
                return originalException;
            }

            return RestorePreparedActive(originalException);
        }

        private Exception RestorePreparedActive(Exception originalException)
        {
            if (!_keptPreparedActive || !_panel)
            {
                return originalException;
            }

            try
            {
                _panel.gameObject.SetActive(_priorActive);
                return originalException;
            }
            catch (Exception cleanupException)
            {
                return Combine(originalException, cleanupException);
            }
        }

        private static Exception Combine(Exception originalException, Exception cleanupException) =>
            originalException == null
                ? cleanupException
                : new AggregateException(originalException, cleanupException);
    }

    internal static Vector2Int GetScreenResolution()
    {
        string[] resolutionStrings = ServiceRepository.GetService<IGraphicsSettingsService>().Resolution.Split(GraphicsResourceManager.ResolutionSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (resolutionStrings.Length >= 2)
        {
            int width = int.Parse(resolutionStrings[0].Trim());
            int height = int.Parse(resolutionStrings[1].Trim());
            return new Vector2Int(width, height);
        }
        return new Vector2Int(0, 0);
    }
    internal static float GetAspectRatio()
    {
        Vector2Int resolution = GetScreenResolution();
        return (float)resolution.x / resolution.y;
    }

    internal static Vector2 GetOverlayCanvasSize()
    {
        if (ServiceRepository.GetService<IGuiService>() is not GuiManager gui)
        {
            return new Vector2(1920, 1080); //Reference Resolution
        }
        return gui.overlayCanvas.GetComponent<RectTransform>().rect.size;
    }
}
