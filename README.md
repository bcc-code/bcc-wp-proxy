# BCC WordPress Proxy

The BCC WordPress Proxy is a "smart" proxy, which provides a scalable front-end for WordPress.

## Background
Quite often, content published via WordPress is fairly static and similar/identical for all users. Often static site generators are used to create a truly static front-end based on a WordPress backend. However, if user authentication is required, this becomes a little more tricky.

WordPress is designed to run in as a single application (i.e. it is not horizontally scalable out of the box). It is possible to run wordpress in a container (making it horizontally scalable) and attach cloud storage, but with this solution, plugin and themes also need to be installed / updated via a code repository (rather than via the WordPress interface), making it harder to manage.

This proxy solution takes a different approach. Instead of trying to make the WordPress instance itself scalable, we place a proxy in front of WordPress. The proxy selectively caches both dynamic and static resources and runs in a container, making it very easy to scale horizontally.

## How Does it Work?
The solutions consists of two parts:

1. **Proxy** 
   *  .Net Core based container (running on a service like Google Cloud Run)
   *  Backed by a distributed cache (e.g. Redis) for storing pages 
   *  Backed distributed storage (e.g. Google Cloud Storage) for storing larger media

2. **Wordpress Plugin**
   * Tracks content changes (used for cache invalidation)
   * Makes last changed date and user account info available to the proxy
   * Automatically authenticates users in WordPress based on user context passed from the proxy.

Users are authenticated by the proxy, and are then mapped to either a specific (e.g. admin) or shared/global (e.g. "member") user in WordPress. By mapping multiple actual users to  shared/global accounts in WordPress, it is possible for different users to benefit from the same cache.

GET requests are typically cached, whereas post requests, or any request to a /wp-admin/* URL is not cached and is passed directly to the backend. WordPress is regularly polled for changes, and caches are invalidated when content is updated.

The OpenID Connect / OAuth tokens retrieved by the proxy are made available for consumption by front-end scripts - allowing for personalized widgets without sacrificing a shared cache for content published from WordPress.

