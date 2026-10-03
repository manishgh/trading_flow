# Android background push preparation

Prepared 28 September 2026. Firebase is not configured and this release does not send background push. Existing notifications come from connected polling and durable preparation-event replay after reconnecting. The phone acceptance results do not establish Firebase delivery.

## Setup needed from the project owner

1. Create a Firebase project and register the production Android application ID `com.manishgh.tradingflow`. If testing a separate acceptance build, register `com.manishgh.tradingflow.acceptance` as a separate Android app in that project too.
2. Download the matching `google-services.json` for each application. Keep the production and acceptance configurations separate; never use the acceptance package as the production registration.
3. Enable Firebase Cloud Messaging HTTP v1. Give the backend's service identity permission to send messages. Prefer application-default credentials or workload identity. Keep service-account private keys on the server, outside source control and the APK.
4. Provide a stable HTTPS TradingFlow backend reachable by the phone. The local USB-forwarded address used for acceptance is not a background delivery deployment.
5. Allow notifications on the device. Android 13 and later require runtime notification permission; delivery is also affected by user/OEM battery restrictions. A force-stopped application is a distinct acceptance case and must not be described as guaranteed delivery.

## Integration design prepared for implementation

Default state is disabled / not configured. Do not silently pretend local polling is push. Firebase activation must require a matching Android app configuration, backend project ID, sender credentials and an authenticated registered device.

### Android adapter

- Add a maintained Firebase Messaging binding compatible with the project's .NET Android target, conditionally including the matching Google Services configuration. Verify both configuration-free and configured Release builds.
- Handle token creation and rotation in the messaging service. Register a token only after successful TradingFlow sign-in; retry registration after reconnecting. Revoke on sign-out/backend change and expire stale registrations server-side. Never log full tokens.
- Foreground messages use the existing local notification display. Background notification-plus-data messages carry exact identifiers in the launch intent. Map those fields to `CandidateNavigation` or `PreparationNotifications`, preserving their backend check, authentication recovery and exact-revision behavior.
- Use a stable event identifier for deduplication and a distinct notification/PendingIntent identity per target. Multiple alerts must not overwrite one another. Do not accept executable URLs or order instructions from a notification.
- Keep durable polling as reconciliation. A push message is a wake-up hint; the authenticated backend remains the source of truth.

### Authenticated backend boundary

Proposed routes below are design proposals, not currently available endpoints:

- `POST /api/v1/notification-devices`: register or rotate an installation's token, with CSRF protection. Derive the operator from the authenticated principal; never accept a client-supplied owner.
- `DELETE /api/v1/notification-devices/{installationId}`: revoke the current operator's registration.
- Registration should bind operator, installation, Firebase project/application, backend identity, token, last-seen time and revocation state. Encrypt tokens at rest and reject ownership changes without an authenticated replacement registration.

Build delivery from committed preparation/candidate events. Persist per-device delivery attempts with a unique `(event, device)` identity, bounded retry/backoff, expiry and terminal failure. Revalidate ownership and revocation before dispatch. Retire invalid/unregistered tokens. Do not register a sender hosted service while configuration is incomplete.

Preparation events already retain exact workflow/occurrence/revision IDs. Candidate events need an explicit, durable recipient subscription; global candidate projection events must not be broadcast to every operator. A dispatcher must never enqueue an order, authorize a strategy, or alter candidate admission.

### Payload mapping

All FCM data values are strings. Keep notification text generic to avoid exposing holdings or strategy details on a locked screen.

| Kind | Required data | Existing destination |
|---|---|---|
| Preparation | eventId, backend, workflowId, occurrenceId, revisionId | Exact preparation snapshot |
| Candidate | eventId, backend, runId, candidateId; optional articleId | Exact candidate audit and attached article |

Malformed IDs, unknown kinds, a different backend, missing evidence or failed authorization must produce an unavailable/sign-in state. Never substitute the newest run or snapshot.

## Activation acceptance

With the real Firebase project configured, verify foreground, background and cold-start delivery on the physical phone; permission denial; offline delivery/expiry; token rotation; account/backend changes; duplicate messages; simultaneous alerts; reserved characters in article IDs; and exact evidence recovery after sign-in. Verify no message or notification interaction can confirm an order. Record actual Firebase message and durable event identities in sanitized evidence.

Until the adapter, authenticated registration, durable dispatcher and these acceptance checks are implemented, background push remains pending. Project configuration alone is not enough to call it finished.

## Primary references

- [Firebase Android client setup](https://firebase.google.com/docs/cloud-messaging/android/get-started)
- [Foreground/background message handling](https://firebase.google.com/docs/cloud-messaging/android/receive-messages)
- [HTTP v1 sending and server authorization](https://firebase.google.com/docs/cloud-messaging/send/v1-api)
