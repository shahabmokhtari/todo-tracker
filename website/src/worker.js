// The site lives at onething.awesomehq.app. Its old address (from when the app was called Todo Tracker) still works:
// it sends people to the same page at the new one. Everything else is the static files in public/.
const OLD_HOST = 'todotracker.awesomehq.app';
const HOST = 'onething.awesomehq.app';

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.hostname === OLD_HOST) {
      url.hostname = HOST;
      return Response.redirect(url.toString(), 301);
    }
    return env.ASSETS.fetch(request);
  },
};
