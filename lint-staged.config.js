export default {
  'Frontend/**/*.{ts,tsx,js,jsx,css,md,json,html}': (files) => {
    const quoted = files.map((f) => `"${f}"`).join(' ');
    return [
      `Frontend/node_modules/.bin/prettier --write ${quoted}`,
      `Frontend/node_modules/.bin/eslint --config Frontend/eslint.config.js --fix ${quoted}`,
    ];
  },
  '!(libobs-sharp)/**/*.cs': (files) => {
    const quoted = files.map((f) => `"${f}"`).join(' ');
    // Run dotnet format on the whole solution - more reliable than per-file
    return [`dotnet format Segra.sln --exclude libobs-sharp`];
  },
};
