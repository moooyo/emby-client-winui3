# App state tests

This cross-platform `net10.0` project links the production library, media card,
media shelf, and image cache sources. Tests exercise the real `EmbyApiClient`
against controlled HTTP responses, including cancellation and staged refreshes.

`Visibility.cs` is the only WinUI substitute. It supplies a data enum so these
tests can verify ViewModel state without loading Windows App SDK. The project
does not validate WinUI binding, dispatcher affinity, rendering, focus, scrolling,
or layout behavior.

Run in the authorized test environment from the repository root:

```sh
dotnet test --project tests/EmbyClient.AppState.Tests/EmbyClient.AppState.Tests.csproj
```

Response gates use `TaskCompletionSource` to make intermediate states observable;
they do not rely on sleeps or server timing. Request gate timeouts only bound
failures when an expected request never arrives.
