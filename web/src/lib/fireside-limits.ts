/** Fireside limits from contract v1.22, shared by the screens and the guide. */
export const firesideLimits = {
  /** Most characters a Fireside name may have after trimming. */
  name: 32,
  /** Characters in a code, spaces left out. */
  codeLength: 8,
  /** How long a code works, in minutes. */
  codeMinutes: 30,
  /** Most friends at one fireside. */
  friends: 20,
} as const;
