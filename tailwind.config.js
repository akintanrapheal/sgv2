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
        // Subtle warm off-white page canvas — matches the Featured Pieces section (bg-stone-50)
        // so the whole storefront shares that soft near-white shade.
        canvas: '#fafaf9',
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
        brand: {
          50:  '#fde9f5',
          100: '#fbc8e9',
          200: '#f796d3',
          300: '#f35fb8',
          400: '#ef289e',
          500: '#ed028b',
          600: '#c90278',
          700: '#a0025f',
          800: '#770147',
          900: '#4e012f',
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
