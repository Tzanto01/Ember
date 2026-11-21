(function () {
  var themes = [
    ['hearthlight', '#fbf5ec', '#1c1410'],
    ['fieldnotes', '#f6f3e7', '#121914'],
    ['tidepool', '#eef6f9', '#0a171f'],
    ['marigold', '#fcf6de', '#18130c'],
  ];
  var root = document.documentElement;
  var stored = null;
  try {
    stored = localStorage.getItem('ember.theme');
  } catch (e) {}
  var theme = themes[0];
  for (var i = 0; i < themes.length; i++) if (themes[i][0] === stored) theme = themes[i];
  root.setAttribute('data-theme', theme[0]);
  try {
    if (localStorage.getItem('ember.calm') === '1') root.setAttribute('data-calm', '');
  } catch (e) {}
  var query = window.matchMedia('(prefers-color-scheme: dark)');
  function apply() {
    var scheme = null;
    try {
      scheme = localStorage.getItem('ember.colorScheme');
    } catch (e) {}
    var dark = scheme === 'dark' || (scheme !== 'light' && query.matches);
    root.classList.toggle('dark', dark);
    root.style.setProperty('background-color', 'var(--background, ' + theme[dark ? 2 : 1] + ')');
  }
  apply();
  query.addEventListener('change', apply);
})();
