// SPDX-License-Identifier: GPL-3.0-only

namespace IntroSkipper.Data;

/// <summary>
/// Summary of a bulk sidecar <c>.edl</c> synchronization or cleanup pass.
/// </summary>
/// <param name="Written">Files created or updated.</param>
/// <param name="Unchanged">Files whose on-disk content already matched.</param>
/// <param name="Removed">Managed files deleted because segments were cleared, excluded, missing, or EDL writing is disabled.</param>
/// <param name="Failed">Items whose file I/O failed and was logged as a warning.</param>
public sealed record EdlSyncResponse(int Written, int Unchanged, int Removed, int Failed);
