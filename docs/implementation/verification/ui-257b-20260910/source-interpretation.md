# Interpretation of the post-library counters

The schema 3 sample at elapsed 556,003 ms reports 46 subscribed images, 46 loaded images, 46 attached images, two application-tracked realized containers, and two bound sources after returning to Home. These measurements do not establish a leak or a new correctness defect.

`_posterSubscriptions` is a strong-key dictionary. `Poster_Unloaded` removes registrations; recycling and collection Reset cancel work and clear sources while retaining registrations for still-loaded controls. The observed 47 unique weakly tracked images across 336 loaded records is compatible with container reuse. It does not demonstrate one retained control per record. See `src/EmbyClient.App/Views/LibraryView.xaml.cs`, registration and recycling around lines 176-214, unloading around line 305, and Reset cleanup around line 403.

`AttachedPosterControlsCount` only checks for a `GridViewItem` ancestor. It does not establish attachment to the current `MediaGrid` or `XamlRoot`, visibility, or viewport intersection. `RealizedPosterControlsCount` checks whether that ancestor has an entry in the application's `_posterContainers` table. Neither count is an independent census of native realized containers. `BoundPosterSourcesCount` covers subscribed images whose `Source` is non-null; the observed reduction to two supports scoped source cleanup even though other controls remain subscribed. See `LibraryView.xaml.cs` around line 239 and `tools/EmbyClient.LibraryObservation/LibraryView.Observation.cs` around lines 175-180.

The observer's image and bitmap registries use weak references. The `DetailPoster` can be recorded when it is explicitly cleared, independently of grid subscriptions. The 376 tracked bitmap targets and 126 resolving targets at this sample show that 250 recorded targets no longer resolve. They do not identify the owners of the remaining 126 or establish which targets predate the latest Gen 2 collection. No native/GPU resource count is collected.

Decode accounting at the sample is balanced: 440 started, 376 completed, 64 canceled, zero failed, and zero active. There is no sampled outstanding decode backlog. Cancellation counts identify managed cancellation handling, not native resource-release confirmation. The presentation-clock and position-update counters are zero throughout this no-video segment.

This was a read-only review prompted by the new runtime counters. No product code change, forced collection, test, build, or desktop action was performed by the review.
