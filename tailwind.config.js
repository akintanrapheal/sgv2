/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    './src/SterlingLams.Web/Views/**/*.cshtml',
    './src/SterlingLams.Web/Areas/**/*.cshtml',
    './src/SterlingLams.Web/Pages/**/*.cshtml',
    './src/SterlingLams.Web/wwwroot/js/**/*.js',
  ],
  theme: {
    extend: {
      fontFamily: {
        cormorant: ['"Cormorant Garamond"', 'Georgia', 'serif'],
        inter: ['Inter', 'system-ui', 'sans-serif'],
      },
      colors: {
        gold: {
          50:  '#fdf9f0',
          100: '#faefd4',
          200: '#f4d98d',
          300: '#ecc54a',
          400: '#e2ac1f',
          500: '#c99210',
          600: '#a87209',
          700: '#85550c',
          800: '#6d4411',
          900: '#5a3812',
        },
      },
      letterSpacing: {
        'extra-wide': '0.3em',
        'ultra-wide': '0.5em',
      },
    },
  },
  plugins: [],
};
