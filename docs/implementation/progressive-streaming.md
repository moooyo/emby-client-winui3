# Progressive HTTP transport implementation

The Windows adapter now has a streaming receive path for a negotiated `Transcode` request whose timeline is `ProgressiveSegment`. The normal application still requests its existing HLS profile. This change does not automatically switch protocols after a failure, change the capability profile, or claim that native progressive MP4 playback has passed visual acceptance.

## Why a separate receive path is needed

A [protocol control](../../tools/EmbyClient.SubtitleFixtures/verification/progressive-prefix-control.json) against the owned Emby 4.9.5.0 server negotiated the original ASS fixture with the same video, audio, subtitle, and bitrate settings, changing only the transcoding profile from TS/HLS to MP4/HTTP. Both zero-second and 41-second starts returned an MP4 transcode with subtitle delivery `Encode`. The server ignored the bounded Range request and returned `200`, `Transfer-Encoding: chunked`, no Content-Length or Content-Range, and `Accept-Ranges: none`. Each control read only 64 KiB before closing and cleaning up its encoding session; the prefixes contained `ftyp`, `moov`, and `moof` boxes.

Those observations establish an HTTP response contract, not native decoding, subtitle pixels, or complete playback. The original Range path intentionally requires a known-length 206 response and cannot represent this stream. The new path preserves the observed 200 semantics instead of guessing a length or manufacturing a partial response.

## Ownership and HTTP behavior

`ScopedMediaTransport.OpenStreamAsync` returns a `MediaStreamLease` that owns the real response, response stream, cancellation registrations, and transport request lease. The active request remains tracked until the response and in-flight reads have drained. Reads are serialized and have a bounded idle timeout. Cancellation closes the response to interrupt a pending read; concurrent disposal shares one completion task.

`ProgressiveHttpRelay` exposes a private IPv4 loopback URI with a random path. It accepts only that exact target and authority, GET or HEAD, an empty request body, and at most one valid byte range. It admits four clients, limits request headers to 16 KiB, and uses one 64 KiB copy buffer per active response. Each read is followed by its corresponding awaited write, so a slow consumer applies backpressure without an unbounded application buffer.

| Upstream behavior | Local response |
| --- | --- |
| 200 ignores a Range request | Preserve 200 and the complete response body |
| 200 with unknown length | Stream using chunked framing for HTTP/1.1; reject HTTP/1.0 GET before sending success |
| Valid 206 | Preserve the actual status, Content-Range, and declared length when supplied |
| HEAD | Send upstream HEAD without Range and return metadata without a body |
| 416 | Preserve the range rejection and Content-Range without declaring a playback failure |
| Authentication, permission, or server failure | Preserve the status, suppress the error body, and report one fixed failure category |
| Successful response with both Transfer-Encoding and Content-Length, or an unsupported transfer coding | Reject before forwarding success or body bytes, with 502 and `UnsupportedFormat` |
| Truncated body or malformed chunk termination | Abort the response and report failure; never emit a successful terminal chunk |

Only the fixed source's scoped headers reach its origin. Redirects reuse the existing origin checks and remove credential query parameters when crossing origins. Cookies, default credentials, and automatic redirects remain disabled. Downstream credentials are not forwarded upstream. Socket resets, local request rejection, and owner cancellation do not masquerade as upstream playback failures.

The receive path accepts an absent Transfer-Encoding header or exactly one unparameterized `chunked` coding. .NET removes chunk framing but does not decode arbitrary transfer codings; accepting `gzip, chunked` would otherwise forward encoded bytes with their coding removed. A Content-Length combined with chunked framing could also make a downstream consumer accept only a prefix as a complete response. These ambiguous combinations are rejected. HTTP/1.0 GET requires a declared Content-Length because a connection-close body cannot reliably distinguish an interrupted stream from normal completion; HEAD still returns metadata without a body.

After an over-budget header is rejected, a short, bounded drain prevents unread request bytes from causing a Windows TCP reset that discards the 431 response. This drain is limited to 100 ms and 16 KiB; it does not accept another request or start an upstream transfer.

## Native adapter integration

`NativePlaybackEngine` selects the new relay only for the progressive transcode timeline. HLS continues through `ScopedAdaptiveHttpFilter`; original full-source media continues through `SessionHttpRelay` and its strict Range cache. The progressive URI extension comes from the negotiated `TranscodingContainer`, not the original source's container.

The relay belongs to the same session lifetime as the player source. Retirement cancels that lifetime, detaches the native source, drains the relay, and then drains the scoped transport. Existing playback IDs, shared-player ownership, and failure classification remain authoritative. A queued native Ended callback checks for an already recorded relay failure before publishing success, preventing that known error from being converted into episode continuation. The actual native dispatcher race remains a deferred runtime check.

The coordinator already distinguishes a progressive segment's engine clock from the original item clock. It starts the engine at zero and adds the validated server trim offset to reports. Logical seeking retires the previous stream and negotiates another source segment; it does not assume that an unknown-length response supports arbitrary byte seeks.

## Verification and remaining work

The MediaTransport Release suite passes 120 cases: the original 73 plus 47 progressive relay cases, with zero failures or warnings. Raw TCP tests cover early delivery before upstream EOF, unknown-length and truncated responses, Range/HEAD/status preservation, header and client limits, origin isolation, half-close/reset behavior, cancellation during headers and body reads, and concurrent disposal with upstream connections and transport leases drained. Framing regressions reject TE plus Content-Length, unsupported codings, duplicate or parameterized chunked fields, and invalid appended TE fields that a typed header collection would otherwise omit. HTTP/1.0 tests cover rejection of unknown-length GET while retaining known-length GET and HEAD.

The first automated run exposed the 431 reset issue; the fixed implementation passes its exact boundary regression. A separate test correction uses a real TCP reset rather than assuming that ordinary `TcpClient.Dispose` sends one.

Desktop control and native playback tests are paused at the user's request. Native progressive rendering, source-frame agreement, subtitles, true Ended behavior, and repeated native resource cycles remain unverified. The [profile-control harness](../../tools/EmbyClient.NativeProbe/PROGRESSIVE-CONTROL.md) is separate from the default product policy. The earlier ASS/PGS HLS failures remain retained; a compiling streaming transport does not turn those failures into passes.
