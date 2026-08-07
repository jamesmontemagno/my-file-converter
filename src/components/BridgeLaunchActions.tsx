import { useState } from 'react';
import {
  bridgeLaunchCommand,
  copyBridgeLaunchCommand,
} from '../bridge/launch';

export function BridgeLaunchActions() {
  const [copyStatus, setCopyStatus] = useState<'idle' | 'copied' | 'failed'>('idle');

  async function copyCommand() {
    try {
      await copyBridgeLaunchCommand();
      setCopyStatus('copied');
    } catch {
      setCopyStatus('failed');
    }
  }

  return (
    <div className="bridge-launch-actions">
      <button type="button" className="bridge-download-action" onClick={() => void copyCommand()}>
        {copyStatus === 'copied' ? 'Command copied' : 'Copy bridge command'}
      </button>
      <p className="bridge-launch-note" aria-live="polite">
        {copyStatus === 'failed'
          ? 'Copy was blocked. Select the command below and paste it into your terminal.'
          : 'Run this command in a terminal to start the bridge.'}
      </p>
      <pre>
        <code>{bridgeLaunchCommand}</code>
      </pre>
    </div>
  );
}
