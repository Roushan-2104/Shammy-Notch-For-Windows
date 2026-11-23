// Shared by the story and the practice desktop: the alarm skies for each hour and the sentence that gets dictated.
export const SKIES = {
  dawn: { name: 'Dawn', top: '#1E1626', mid: '#5A2E3A', hor: '#FFB070', core: '#FFF6E6', edge: '#FFA860', glow: '255,186,120', stop: '#FFF1E4', moon: false },
  day: { name: 'Day', top: '#16304F', mid: '#2F5C88', hor: '#E9C48C', core: '#FFFFFF', edge: '#FFE2A8', glow: '255,236,190', stop: '#FFFFFF', moon: false },
  sunset: { name: 'Sunset', top: '#170F2E', mid: '#5E1E4C', hor: '#F2504A', core: '#FFE0C8', edge: '#F04A32', glow: '255,110,90', stop: '#FFE9DE', moon: false },
  night: { name: 'Night', top: '#04050B', mid: '#0A1226', hor: '#1C2C50', core: '#F4F7FF', edge: '#B8C8F0', glow: '170,195,255', stop: '#E9EEFF', moon: true },
};
export const skyAt = (h) => (h >= 5 && h < 9 ? SKIES.dawn : h >= 9 && h < 16 ? SKIES.day : h >= 16 && h < 19 ? SKIES.sunset : SKIES.night);
export const fmtHour = (h) => `${h % 12 === 0 ? 12 : h % 12}:30 ${h < 12 ? 'AM' : 'PM'}`;
export const ROMAN = ['haan,', 'kal', '6', 'baje', 'cafe', 'pe', 'milte', 'hain'];
export const DEVA = ['हाँ,', 'कल', '6', 'बजे', 'कैफ़े', 'पे', 'मिलते', 'हैं'];
