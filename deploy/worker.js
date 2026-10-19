export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/ygocalc") {
      url.pathname = "/ygocalc/";
      return Response.redirect(url, 302);
    }
    if (url.pathname === "/") {
      url.pathname = "/ygocalc/";
      return Response.redirect(url, 302);
    }
    if (!env?.ASSETS) {
      return new Response("UI assets are not configured on this worker.", {
        status: 404,
        headers: { "Content-Type": "text/plain; charset=utf-8" },
      });
    }
    const assetUrl = new URL(request.url);
    if (assetUrl.pathname.startsWith("/ygocalc/")) {
      const stripped = assetUrl.pathname.slice("/ygocalc".length);
      assetUrl.pathname = stripped === "" ? "/" : stripped;
    }
    return env.ASSETS.fetch(new Request(assetUrl, request));
  },
};
