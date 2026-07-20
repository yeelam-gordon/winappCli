// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Windows.Win32.UI.Accessibility;

namespace WinApp.Cli.Services;

internal sealed partial class UiAutomationService
{
    private (List<string> AncestorTypes, long NativeWindowHandle) GetScopedAncestorContext(
        IUIAutomationElement target,
        IUIAutomationElement root,
        int maxAncestors,
        UiTraversalState traversal,
        string selector,
        long fallbackWindowHandle)
    {
        var ancestorTypes = new List<string>();
        var nativeWindowHandle = 0L;

        if (!traversal.Checkpoint(selector))
        {
            return (ancestorTypes, fallbackWindowHandle);
        }

        try
        {
            if (_automation.CompareElements(target, root))
            {
                return (ancestorTypes, fallbackWindowHandle);
            }
        }
        catch (Exception ex)
        {
            RecordScopedAncestorFailure(traversal, selector, ex);
            return (ancestorTypes, fallbackWindowHandle);
        }

        if (!traversal.Checkpoint(selector))
        {
            return (ancestorTypes, fallbackWindowHandle);
        }

        IUIAutomationTreeWalker walker;
        try
        {
            walker = _automation.get_ControlViewWalker();
        }
        catch (Exception ex)
        {
            RecordScopedAncestorFailure(traversal, selector, ex);
            return (ancestorTypes, fallbackWindowHandle);
        }

        var current = target;
        var reachedRoot = false;
        var enumerationFailed = false;
        for (var count = 0; count < maxAncestors; count++)
        {
            if (!traversal.Checkpoint(selector))
            {
                break;
            }

            IUIAutomationElement? parent;
            try
            {
                parent = walker.GetParentElement(current);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordScopedAncestorFailure(traversal, selector, ex);
                enumerationFailed = true;
                break;
            }

            if (parent is null)
            {
                RecordScopedAncestorFailure(traversal, selector);
                enumerationFailed = true;
                break;
            }

            if (!traversal.Checkpoint(selector))
            {
                break;
            }

            string parentType;
            try
            {
                parentType = GetControlTypeName(parent.get_CurrentControlType());
            }
            catch (Exception ex)
            {
                RecordScopedAncestorFailure(traversal, selector, ex);
                enumerationFailed = true;
                break;
            }

            ancestorTypes.Add(parentType);
            if (!traversal.Checkpoint(selector))
            {
                break;
            }

            if (nativeWindowHandle == 0)
            {
                nativeWindowHandle = SafeGetNativeWindowHandle(parent) ?? 0;
            }

            if (!traversal.Checkpoint(selector))
            {
                break;
            }

            try
            {
                reachedRoot = _automation.CompareElements(parent, root);
            }
            catch (Exception ex)
            {
                RecordScopedAncestorFailure(traversal, selector, ex);
                enumerationFailed = true;
                break;
            }

            if (reachedRoot)
            {
                break;
            }

            current = parent;
        }

        if (!reachedRoot
            && !enumerationFailed
            && !traversal.IsStopped
            && ancestorTypes.Count >= maxAncestors)
        {
            traversal.RecordIssue(
                UiInspectionIssueCodes.DepthLimit,
                $"UI Automation traversal reached the {maxAncestors}-ancestor context limit; the selected audit scope's ancestry is incomplete.",
                selector);
        }

        ancestorTypes.Reverse();
        return (
            ancestorTypes,
            nativeWindowHandle != 0 ? nativeWindowHandle : fallbackWindowHandle);
    }

    private void RecordScopedAncestorFailure(
        UiTraversalState traversal,
        string selector,
        Exception? exception = null)
    {
        if (exception is not null)
        {
            _logger.LogDebug(exception, "UIA ancestor enumeration failed for scoped audit target");
        }

        traversal.RecordIssue(
            UiInspectionIssueCodes.AncestorEnumeration,
            "UI Automation could not enumerate the selected element's ancestor context; chrome classification and native window ownership may be incomplete.",
            selector);
    }
}
