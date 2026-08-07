# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Primary users are everyday people who need to convert a file quickly and safely without needing
technical media knowledge. This audience is inferred from the existing product and current redesign
request because no user interview response was available.

## Product Purpose

LocalMorph converts image, audio, and video files on the user's device. Success is a fast,
understandable conversion flow that gives people confidence their files are not uploaded to a
managed application server.

## Positioning

LocalMorph combines browser-native conversion with an optional, explicitly started LocalMorph
Bridge that uses a locally installed FFmpeg executable. Both routes keep conversion on the user's
device and disclose the route being used.

## Operating Context

People arrive with a file they need in a different format, choose an output, convert it, review
the result, and download it. Advanced users can pair a running loopback bridge and FFmpeg for
broader local conversion support.

## Capabilities and Constraints

- A React, TypeScript, Vite PWA deployed as a static site.
- Browser-native image, audio, and video conversion paths remain available as fallback.
- The optional bridge is a user-started loopback service, not a managed backend or a desktop app.
- FFmpeg must be installed separately and on the bridge process PATH.
- Privacy claims must accurately distinguish browser processing from same-device bridge processing.

## Brand Commitments

The product name is LocalMorph. The voice should be direct, calm, privacy-forward, and free of
unsubstantiated performance or security claims.

## Evidence on Hand

- Existing product logo: `src/Logo.tsx`.
- Existing product screenshot: `image.png`.
- No customer testimonials, performance benchmarks, pricing data, or third-party validation may be
  fabricated.

## Product Principles

- Make the safe, local path obvious before explaining implementation details.
- Help users act quickly without hiding advanced capability.
- Explain conversion routes plainly and truthfully.
- Keep the experience useful when the optional bridge is unavailable.

## Accessibility & Inclusion

Use semantic controls, keyboard-accessible navigation, visible focus states, readable contrast, and
responsive layouts for narrow screens.
