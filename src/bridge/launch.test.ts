import { describe, expect, it } from 'vitest';
import { bridgeLaunchCommand } from './launch';

describe('bridge launch helpers', () => {
  it('uses the published .NET tool command', () => {
    expect(bridgeLaunchCommand).toBe('dnx LocalMorph.Bridge');
  });
});
