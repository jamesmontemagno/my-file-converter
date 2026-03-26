import { describe, expect, it } from 'vitest';

import { CONVERSION_CANCELED_MESSAGE, createConversionAbortError, isConversionAbortError } from './conversion';

describe('conversion abort helpers', () => {
  it('creates a DOMException with AbortError metadata', () => {
    const error = createConversionAbortError();

    expect(error).toBeInstanceOf(DOMException);
    expect(error.name).toBe('AbortError');
    expect(error.message).toBe(CONVERSION_CANCELED_MESSAGE);
  });

  it('identifies conversion cancel errors from DOMException and Error', () => {
    const domAbortError = createConversionAbortError();
    const namedAbortError = new Error('Different message');
    namedAbortError.name = 'AbortError';
    const messageAbortError = new Error(CONVERSION_CANCELED_MESSAGE);

    expect(isConversionAbortError(domAbortError)).toBe(true);
    expect(isConversionAbortError(namedAbortError)).toBe(true);
    expect(isConversionAbortError(messageAbortError)).toBe(true);
    expect(isConversionAbortError(new Error('Other error'))).toBe(false);
  });
});
