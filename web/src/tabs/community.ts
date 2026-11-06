import type { Tab } from "../types.ts";
import { configStore } from "../store/config-store.ts";
import { htmlEl } from "../components/dom.ts";
import { configForm } from "../components/config-form.ts";

export const communityTab: Tab = {
    id: "community",
    label: "Community",
    render(container, signal) {
        const form = configForm(signal);
        const enabled = () => configStore.get("EnableCommunityFallback") === true;

        container.append(
            htmlEl(
                "div",
                { className: "field-description" },
                "<p>Local analysis always runs first. Only an episode or movie it settles <b>without</b> an intro or credits segment is looked up in the community databases, one request per item. A community result never replaces a local or user segment.</p>" +
                    "<p>Community timestamps receive the same boundary adjustments as detected ones, so the Segment Offset Adjustment settings on the Detection tab apply to them.</p>" +
                    "<p>Lookups use the series' IMDb, TMDB and TVDB ids. Enabling or changing these settings re-checks intro and credits results once on the next scan.</p>",
            ),
            form.field("EnableCommunityFallback"),
            form.field("CommunityUseIntroDb", { visible: enabled }),
            form.field("CommunityUseTheIntroDb", { visible: enabled }),
            form.field("CommunityDurationToleranceSeconds", { visible: enabled }),
        );
    },
};
