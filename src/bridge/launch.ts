export const bridgeLaunchCommand = 'dnx LocalMorph.Bridge';

export async function copyBridgeLaunchCommand() {
  await navigator.clipboard.writeText(bridgeLaunchCommand);
}
