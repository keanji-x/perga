# app/router

Code-based TanStack Router. `createRouteTree` and `createAppRouter` build the tree
with injected transport and QueryClient. The production basepath is `/`, matching
Vite and the PWA manifest. No legacy mount or compatibility redirect is installed.

| Path | Surface |
| --- | --- |
| `/` | Today daily Track and calendar |
| `/area/$areaId/new` | New Track in the selected Area |
| `/track/$trackId` | Track report, inventory and conversations |
| `/settings` | Settings landing page |
| `/settings/planners` | Planner configuration |
| `/settings/appearance` | Theme and appearance |
| `/settings/about` | Build and Host information |

The index loader primes areas; the workspace's Track queries remain lazy so one
slow Area cannot block the route commit. Area is a sidebar disclosure group,
not a separate page. `useGo` owns application navigation and root-path links.

`track-route.tsx` and `track-route-body.tsx` compose the Track feature and
conversation panel. Conversation pane/action/initial-send modules carry their
specific assembly responsibilities. Card creation belongs to the Track feature's
new-card controller. Business rules and generic UI primitives stay below app.

Router tests inject controlled transports and exercise real route trees,
including confirmed Track identity across reload, execution-target isolation,
conversation liveness and accessible panel navigation.
