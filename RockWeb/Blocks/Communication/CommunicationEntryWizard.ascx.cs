﻿// <copyright>
// Copyright by the Spark Development Network
//
// Licensed under the Rock Community License (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.rockrms.com/license
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;

using Rock;
using Rock.Attribute;
using Rock.Data;
using Rock.Model;
using Rock.Security;
using Rock.Web.Cache;
using Rock.Web.UI;
using Rock.Web.UI.Controls;

namespace RockWeb.Blocks.Communication
{
    /// <summary>
    /// 
    /// </summary>
    [DisplayName( "Communication Entry Wizard" )]
    [Category( "Communication" )]
    [Description( "Used for creating and sending a new communications such as email, SMS, etc. to recipients." )]

    [SecurityAction( Authorization.APPROVE, "The roles and/or users that have access to approve new communications." )]

    [BinaryFileTypeField( "Binary File Type", "The FileType to use for images that are added to the email using the image component", true, Rock.SystemGuid.BinaryFiletype.COMMUNICATION_IMAGE, order: 1 )]
    [IntegerField( "Character Limit", "Set this to show a character limit countdown for SMS communications. Set to 0 to disable", false, 160, order: 2 )]

    [LavaCommandsField( "Enabled Lava Commands", "The Lava commands that should be enabled for this HTML block.", false, order: 3 )]
    [CustomCheckboxListField( "Communication Types", "The communication types that should be available to user to send (If none are selected, all will be available).", "Recipient Preference,Email,SMS", false, order: 4 )]
    [IntegerField( "Maximum Recipients", "The maximum number of recipients allowed before communication will need to be approved.", false, 300, "", order: 5 )]
    [BooleanField( "Send When Approved", "Should communication be sent once it's approved (vs. just being queued for scheduled job to send)?", true, "", 6 )]
    [IntegerField( "Max SMS Image Width", "The maximum width (in pixels) of an image attached to a mobile communication. If its width is over the max, Rock will automatically resize image to the max width.", false, 600, order: 7 )]
    [LinkedPage( "Simple Communication Page", "The page to use if the 'Use Simple Editor' panel heading icon is clicked. Leave this blank to not show the 'Use Simple Editor' heading icon", false, order: 8 )]
    public partial class CommunicationEntryWizard : RockBlock, IDetailBlock
    {
        #region Properties

        /// <summary>
        /// Gets or sets the individual recipient person ids.
        /// </summary>
        /// <value>
        /// The individual recipient person ids.
        /// </value>
        protected List<int> IndividualRecipientPersonIds
        {
            get
            {
                var recipients = ViewState["IndividualRecipientPersonIds"] as List<int>;
                if ( recipients == null )
                {
                    recipients = new List<int>();
                    ViewState["IndividualRecipientPersonIds"] = recipients;
                }

                return recipients;
            }

            set
            {
                ViewState["IndividualRecipientPersonIds"] = value;
            }
        }

        #endregion

        #region Base Control Methods

        /// <summary>
        /// Raises the <see cref="E:System.Web.UI.Control.Init" /> event.
        /// </summary>
        /// <param name="e">An <see cref="T:System.EventArgs" /> object that contains the event data.</param>
        protected override void OnInit( EventArgs e )
        {
            base.OnInit( e );

            // Tell the browsers to not cache. This will help prevent browser using stale communication wizard stuff after navigating away from this page
            Page.Response.Cache.SetCacheability( System.Web.HttpCacheability.NoCache );
            Page.Response.Cache.SetExpires( DateTime.UtcNow.AddHours( -1 ) );
            Page.Response.Cache.SetNoStore();

            // this event gets fired after block settings are updated. it's nice to repaint the screen if these settings would alter it
            this.BlockUpdated += Block_BlockUpdated;
            this.AddConfigurationUpdateTrigger( upnlContent );

            componentImageUploader.BinaryFileTypeGuid = this.GetAttributeValue( "BinaryFileType" ).AsGuidOrNull() ?? Rock.SystemGuid.BinaryFiletype.DEFAULT.AsGuid();
            hfSMSCharLimit.Value = ( this.GetAttributeValue( "CharacterLimit" ).AsIntegerOrNull() ?? 160 ).ToString();

            gIndividualRecipients.DataKeyNames = new string[] { "Id" };
            gIndividualRecipients.GridRebind += gIndividualRecipients_GridRebind;
            gIndividualRecipients.Actions.ShowAdd = false;
            gIndividualRecipients.ShowActionRow = false;

            btnUseSimpleEditor.Visible = !string.IsNullOrEmpty( this.GetAttributeValue( "SimpleCommunicationPage" ) );
            pnlHeadingLabels.Visible = btnUseSimpleEditor.Visible;
        }

        /// <summary>
        /// Raises the <see cref="E:System.Web.UI.Control.Load" /> event.
        /// </summary>
        /// <param name="e">The <see cref="T:System.EventArgs" /> object that contains the event data.</param>
        protected override void OnLoad( EventArgs e )
        {
            base.OnLoad( e );

            // register navigation event to enable support for the back button
            var scriptManager = ScriptManager.GetCurrent( Page );
            scriptManager.Navigate += scriptManager_Navigate;

            if ( !Page.IsPostBack )
            {
                hfNavigationHistoryInstance.Value = Guid.NewGuid().ToString();
                ShowDetail( PageParameter( "CommunicationId" ).AsInteger() );
            }

            // set the email preview visible = false on every load so that it doesn't stick around after previewing then navigating
            pnlEmailPreview.Visible = false;
        }

        /// <summary>
        /// Handles the Navigate event of the scriptManager control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="HistoryEventArgs"/> instance containing the event data.</param>
        private void scriptManager_Navigate( object sender, HistoryEventArgs e )
        {
            var navigationPanelId = e.State["navigationPanelId"];
            var navigationHistoryInstance = e.State["navigationHistoryInstance"];
            Panel navigationPanel = null;

            if ( navigationHistoryInstance == null & navigationPanelId == null )
            {
                // if scriptManager_Navigate was called with no state, that is probably a navigation to the first step
                navigationHistoryInstance = hfNavigationHistoryInstance.Value;
                navigationPanelId = pnlRecipientSelection.ID;
            }

            if ( navigationPanelId != null )
            {
                navigationPanel = this.FindControl( navigationPanelId ) as Panel;
            }

            if ( navigationHistoryInstance == hfNavigationHistoryInstance.Value && navigationPanel != null )
            {
                if ( navigationPanel == pnlConfirmation )
                {
                    // if navigating history is pnlConfirmation, the communication has already been submitted, so reload the page with the communicationId (if known)
                    Dictionary<string, string> qryParams = new Dictionary<string, string>();
                    if ( hfCommunicationId.Value != "0" )
                    {
                        qryParams.Add( "CommunicationId", hfCommunicationId.Value );
                    }

                    this.NavigateToCurrentPageReference( qryParams );
                }

                var navigationPanels = this.ControlsOfTypeRecursive<Panel>().Where( a => a.CssClass.Contains( "js-navigation-panel" ) );
                foreach ( var pnl in navigationPanels )
                {
                    pnl.Visible = false;
                }

                navigationPanel.Visible = true;
                if ( navigationPanel == pnlTemplateSelection )
                {
                    BindTemplatePicker();
                }

                // upnlContent has UpdateMode = Conditional, so we have to update manually
                upnlContent.Update();
            }
            else
            {
                // mismatch navigation hash, so just reload the without the history hash
                this.NavigateToCurrentPageReference( new Dictionary<string, string>() );
            }
        }

        /// <summary>
        /// Shows the detail.
        /// </summary>
        /// <param name="communicationId">The communication identifier.</param>
        public void ShowDetail( int communicationId )
        {
            Rock.Model.Communication communication = null;
            var rockContext = new RockContext();

            if ( communicationId != 0 )
            {
                communication = new CommunicationService( rockContext ).Get( communicationId );
            }

            var editingApproved = PageParameter( "Edit" ).AsBoolean() && IsUserAuthorized( "Approve" );

            if ( communication == null )
            {
                communication = new Rock.Model.Communication() { Status = CommunicationStatus.Transient };
                communication.SenderPersonAliasId = CurrentPersonAliasId;
                communication.EnabledLavaCommands = GetAttributeValue( "EnabledLavaCommands" );

                var allowedCommunicationTypes = GetAllowedCommunicationTypes();
                if ( !allowedCommunicationTypes.Contains( communication.CommunicationType ) )
                {
                    communication.CommunicationType = allowedCommunicationTypes.First();
                }
            }

            CommunicationStatus[] editableStatuses = new CommunicationStatus[] { CommunicationStatus.Transient, CommunicationStatus.Draft, CommunicationStatus.Denied };
            if ( editableStatuses.Contains( communication.Status ) || ( communication.Status == CommunicationStatus.PendingApproval && editingApproved ) )
            {
                // Make sure they are authorized to view
                bool isCreator = ( communication.CreatedByPersonAlias != null && CurrentPersonId.HasValue && communication.CreatedByPersonAlias.PersonId == CurrentPersonId.Value );
                if ( !communication.IsAuthorized( Rock.Security.Authorization.EDIT, CurrentPerson ) && !isCreator )
                {
                    // not authorized, so hide this block
                    this.Visible = false;
                }
                else
                {
                    // communication is either new or OK to edit
                }
            }
            else
            {
                // Not an editable communication, so hide this block. If there is a CommunicationDetail block on this page, it'll be shown instead
                this.Visible = false;
                return;
            }

            LoadDropDowns();

            hfCommunicationId.Value = communication.Id.ToString();
            lTitle.Text = ( communication.Name ?? "New Communication" ).FormatAsHtmlTitle();

            tbCommunicationName.Text = communication.Name;
            tglBulkCommunication.Checked = communication.IsBulkCommunication;

            tglRecipientSelection.Checked = communication.Id == 0 || communication.ListGroupId.HasValue;
            ddlCommunicationGroupList.SetValue( communication.ListGroupId );

            var segmentDataviewGuids = communication.Segments.SplitDelimitedValues().AsGuidList();
            if ( segmentDataviewGuids.Any() )
            {
                var segmentDataviewIds = new DataViewService( rockContext ).GetByGuids( segmentDataviewGuids ).Select( a => a.Id ).ToList();
                cblCommunicationGroupSegments.SetValues( segmentDataviewIds );
            }

            this.IndividualRecipientPersonIds = new CommunicationRecipientService( rockContext ).Queryable().Where( r => r.CommunicationId == communication.Id ).Select( a => a.PersonAlias.PersonId ).ToList();
            UpdateRecipientFromListCount();
            UpdateIndividualRecipientsCountText();

            // If there aren't any Communication Groups, hide the option and only show the Individual Recipient selection
            if ( ddlCommunicationGroupList.Items.Count <= 1 )
            {
                tglRecipientSelection.Checked = false;
                tglRecipientSelection.Visible = false;
            }

            tglRecipientSelection_CheckedChanged( null, null );

            // Note: Javascript takes care of making sure the buttons are set up based on this
            hfMediumType.Value = communication.CommunicationType.ConvertToInt().ToString();

            tglSendDateTime.Checked = !communication.FutureSendDateTime.HasValue;
            dtpSendDateTime.SelectedDateTime = communication.FutureSendDateTime;
            tglSendDateTime_CheckedChanged( null, null );

            hfSelectedCommunicationTemplateId.Value = communication.CommunicationTemplateId.ToString();

            // Email Summary fields
            tbFromName.Text = communication.FromName;
            ebFromAddress.Text = communication.FromEmail;

            // Email Summary fields: additional fields
            ebReplyToAddress.Text = communication.ReplyToEmail;
            ebCCList.Text = communication.CCEmails;
            ebBCCList.Text = communication.BCCEmails;

            hfShowAdditionalFields.Value = ( !string.IsNullOrEmpty( communication.ReplyToEmail ) || !string.IsNullOrEmpty( communication.CCEmails ) || !string.IsNullOrEmpty( communication.BCCEmails ) ).ToTrueFalse().ToLower();

            tbEmailSubject.Text = communication.Subject;

            //// NOTE: tbEmailPreview will be populated by parsing the Html of the Email/Template

            hfEmailAttachedBinaryFileIds.Value = communication.GetAttachmentBinaryFileIds( CommunicationType.Email ).AsDelimited( "," );
            UpdateEmailAttachedFiles( false );

            // Mobile Text Editor
            ddlSMSFrom.SetValue( communication.SMSFromDefinedValueId );
            tbSMSTextMessage.Text = communication.SMSMessage;

            fupMobileAttachment.BinaryFileId = communication.GetAttachmentBinaryFileIds( CommunicationType.SMS ).FirstOrDefault();
            UpdateMobileAttachedFiles( false );

            // Email Editor
            hfEmailEditorHtml.Value = communication.Message;

            htmlEditor.MergeFields.Clear();
            htmlEditor.MergeFields.Add( "GlobalAttribute" );
            htmlEditor.MergeFields.Add( "Rock.Model.Person" );
            htmlEditor.MergeFields.Add( "Communication.Subject|Subject" );
            htmlEditor.MergeFields.Add( "Communication.FromName|From Name" );
            htmlEditor.MergeFields.Add( "Communication.FromAddress|From Address" );
            htmlEditor.MergeFields.Add( "Communication.ReplyTo|Reply To" );
            htmlEditor.MergeFields.Add( "UnsubscribeOption" );
            if ( communication.AdditionalMergeFields.Any() )
            {
                htmlEditor.MergeFields.AddRange( communication.AdditionalMergeFields );
            }
        }

        /// <summary>
        /// Loads the drop downs.
        /// </summary>
        private void LoadDropDowns()
        {
            var rockContext = new RockContext();

            // load communication group list
            var groupTypeCommunicationGroupId = GroupTypeCache.Read( Rock.SystemGuid.GroupType.GROUPTYPE_COMMUNICATIONLIST.AsGuid() ).Id;
            var groupService = new GroupService( rockContext );

            var communicationGroupList = groupService.Queryable().Where( a => a.GroupTypeId == groupTypeCommunicationGroupId ).OrderBy( a => a.Order ).ThenBy( a => a.Name ).ToList();
            var authorizedCommunicationGroupList = communicationGroupList.Where( g => g.IsAuthorized( Rock.Security.Authorization.VIEW, this.CurrentPerson ) ).ToList();

            ddlCommunicationGroupList.Items.Clear();
            ddlCommunicationGroupList.Items.Add( new ListItem() );
            foreach ( var communicationGroup in authorizedCommunicationGroupList )
            {
                ddlCommunicationGroupList.Items.Add( new ListItem( communicationGroup.Name, communicationGroup.Id.ToString() ) );
            }

            LoadCommunicationSegmentFilters();

            rblCommunicationGroupSegmentFilterType.Items.Clear();
            rblCommunicationGroupSegmentFilterType.Items.Add( new ListItem( "All segment filters", SegmentCriteria.All.ToString() ) { Selected = true } );
            rblCommunicationGroupSegmentFilterType.Items.Add( new ListItem( "Any segment filters", SegmentCriteria.Any.ToString() ) );

            UpdateRecipientFromListCount();

            btnMediumRecipientPreference.Attributes["data-val"] = Rock.Model.CommunicationType.RecipientPreference.ConvertToInt().ToString();
            btnMediumEmail.Attributes["data-val"] = Rock.Model.CommunicationType.Email.ConvertToInt().ToString();
            btnMediumSMS.Attributes["data-val"] = Rock.Model.CommunicationType.SMS.ConvertToInt().ToString();

            var allowedCommunicationTypes = GetAllowedCommunicationTypes();
            btnMediumRecipientPreference.Visible = allowedCommunicationTypes.Contains( CommunicationType.RecipientPreference );
            btnMediumEmail.Visible = allowedCommunicationTypes.Contains( CommunicationType.Email );
            btnMediumSMS.Visible = allowedCommunicationTypes.Contains( CommunicationType.SMS );

            // only show the medium type selection if there is a choice
            rcwMediumType.Visible = allowedCommunicationTypes.Count > 1;

            ddlSMSFrom.BindToDefinedType( DefinedTypeCache.Read( new Guid( Rock.SystemGuid.DefinedType.COMMUNICATION_SMS_FROM ) ), true, true );
        }

        /// <summary>
        /// Loads the communication types that are configured for this block
        /// </summary>
        private List<CommunicationType> GetAllowedCommunicationTypes()
        {
            var communicationTypes = this.GetAttributeValue( "CommunicationTypes" ).SplitDelimitedValues();
            var result = new List<CommunicationType>();
            if ( communicationTypes.Any() )
            {
                // Recipient Preference,Email,SMS
                if ( communicationTypes.Contains( "RecipientPreference" ) )
                {
                    result.Add( CommunicationType.RecipientPreference );
                }

                if ( communicationTypes.Contains( "Email" ) )
                {
                    result.Add( CommunicationType.Email );
                }

                if ( communicationTypes.Contains( "SMS" ) )
                {
                    result.Add( CommunicationType.SMS );
                }
            }
            else
            {
                result.Add( CommunicationType.RecipientPreference );
                result.Add( CommunicationType.Email );
                result.Add( CommunicationType.SMS );
            }

            return result;
        }

        /// <summary>
        /// Handles the BlockUpdated event of the control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void Block_BlockUpdated( object sender, EventArgs e )
        {
            this.NavigateToCurrentPageReference( new Dictionary<string, string>() );
        }

        /// <summary>
        /// Sets the navigation history.
        /// </summary>
        /// <param name="currentPanel">The panel that should be navigated to when this history point is loaded
        private void SetNavigationHistory( Panel navigateToPanel )
        {
            this.AddHistory( "navigationPanelId", navigateToPanel.ID );
            this.AddHistory( "navigationHistoryInstance", hfNavigationHistoryInstance.Value );
        }

        /// <summary>
        /// Handles the Click event of the btnUseSimpleEditor control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnUseSimpleEditor_Click( object sender, EventArgs e )
        {
            int communicationId = hfCommunicationId.Value.AsInteger();
            NavigateToLinkedPage( "SimpleCommunicationPage", "CommunicationId", communicationId );
        }

        #endregion

        #region Recipient Selection

        /// <summary>
        /// Shows the recipient selection.
        /// </summary>
        private void ShowRecipientSelection()
        {
            pnlRecipientSelection.Visible = true;
            SetNavigationHistory( pnlRecipientSelection );
        }

        /// <summary>
        /// Loads the common communication segment filters along with any additional filters that are defined for the selected communication list
        /// </summary>
        private void LoadCommunicationSegmentFilters()
        {
            var rockContext = new RockContext();

            // load common communication segments (each communication list may have additional segments)
            var dataviewService = new DataViewService( rockContext );
            var categoryIdCommunicationSegments = CategoryCache.Read( Rock.SystemGuid.Category.DATAVIEW_COMMUNICATION_SEGMENTS.AsGuid() ).Id;
            var commonSegmentDataViewList = dataviewService.Queryable().Where( a => a.CategoryId == categoryIdCommunicationSegments ).OrderBy( a => a.Name ).ToList();

            var selectedDataViewIds = cblCommunicationGroupSegments.Items.OfType<ListItem>().Where( a => a.Selected ).Select( a => a.Value ).AsIntegerList();

            cblCommunicationGroupSegments.Items.Clear();
            foreach ( var commonSegmentDataView in commonSegmentDataViewList )
            {
                if ( commonSegmentDataView.IsAuthorized( Rock.Security.Authorization.VIEW, this.CurrentPerson ) )
                {
                    cblCommunicationGroupSegments.Items.Add( new ListItem( commonSegmentDataView.Name, commonSegmentDataView.Id.ToString() ) );
                }
            }

            // reselect any segments that they previously select (if they exist)
            cblCommunicationGroupSegments.SetValues( selectedDataViewIds );

            int? communicationGroupId = ddlCommunicationGroupList.SelectedValue.AsIntegerOrNull();
            List<Guid> segmentDataViewGuids = null;
            if ( communicationGroupId.HasValue )
            {
                var communicationGroup = new GroupService( rockContext ).Get( communicationGroupId.Value );
                if ( communicationGroup != null )
                {
                    communicationGroup.LoadAttributes();
                    var segmentAttribute = AttributeCache.Read( Rock.SystemGuid.Attribute.GROUP_COMMUNICATION_LIST_SEGMENTS.AsGuid() );
                    segmentDataViewGuids = communicationGroup.GetAttributeValue( segmentAttribute.Key ).SplitDelimitedValues().AsGuidList();
                    var additionalSegmentDataViewList = dataviewService.GetByGuids( segmentDataViewGuids ).OrderBy( a => a.Name ).ToList();

                    foreach ( var additionalSegmentDataView in additionalSegmentDataViewList )
                    {
                        if ( additionalSegmentDataView.IsAuthorized( Rock.Security.Authorization.VIEW, this.CurrentPerson ) )
                        {
                            cblCommunicationGroupSegments.Items.Add( new ListItem( additionalSegmentDataView.Name, additionalSegmentDataView.Id.ToString() ) );
                        }
                    }
                }
            }

            pnlCommunicationGroupSegments.Visible = cblCommunicationGroupSegments.Items.Count > 0;
        }

        /// <summary>
        /// Handles the Click event of the btnRecipientSelectionNext control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnRecipientSelectionNext_Click( object sender, EventArgs e )
        {
            nbRecipientsAlert.Visible = false;
            if ( tglRecipientSelection.Checked )
            {
                if ( !GetRecipientFromListSelection().Any() )
                {
                    nbRecipientsAlert.Text = "At least one recipient is required.";
                    nbRecipientsAlert.Visible = true;
                    return;
                }
            }
            else
            {
                if ( !this.IndividualRecipientPersonIds.Any() )
                {
                    nbRecipientsAlert.Text = "At least one recipient is required.";
                    nbRecipientsAlert.Visible = true;
                    return;
                }
            }

            pnlRecipientSelection.Visible = false;
            ShowCommunicationDelivery();
        }

        /// <summary>
        /// Handles the CheckedChanged event of the tglRecipientSelection control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void tglRecipientSelection_CheckedChanged( object sender, EventArgs e )
        {
            nbRecipientsAlert.Visible = false;
            pnlRecipientSelectionList.Visible = tglRecipientSelection.Checked;
            pnlRecipientSelectionIndividual.Visible = !tglRecipientSelection.Checked;
        }

        /// <summary>
        /// Handles the SelectPerson event of the ppAddPerson control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void ppAddPerson_SelectPerson( object sender, EventArgs e )
        {
            if ( ppAddPerson.PersonId.HasValue )
            {
                if ( !IndividualRecipientPersonIds.Contains( ppAddPerson.PersonId.Value ) )
                {
                    IndividualRecipientPersonIds.Add( ppAddPerson.PersonId.Value );
                }

                // clear out the personpicker and have it say "Add Person" again since they are added to the list
                ppAddPerson.SetValue( null );
                ppAddPerson.PersonName = "Add Person";
            }

            UpdateIndividualRecipientsCountText();
        }

        /// <summary>
        /// Updates the individual recipients count text.
        /// </summary>
        private void UpdateIndividualRecipientsCountText()
        {
            var individualRecipientCount = this.IndividualRecipientPersonIds.Count();
            lIndividualRecipientCount.Text = string.Format( "{0} {1} selected", individualRecipientCount, "recipient".PluralizeIf( individualRecipientCount != 1 ) );
        }

        /// <summary>
        /// Handles the Click event of the btnViewIndividualRecipients control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnViewIndividualRecipients_Click( object sender, EventArgs e )
        {
            BindIndividualRecipientsGrid();
            mdIndividualRecipients.Show();
        }

        /// <summary>
        /// Handles the GridRebind event of the gIndividualRecipients control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="GridRebindEventArgs"/> instance containing the event data.</param>
        private void gIndividualRecipients_GridRebind( object sender, GridRebindEventArgs e )
        {
            BindIndividualRecipientsGrid();
        }

        /// <summary>
        /// Handles the RowDataBound event of the gIndividualRecipients control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="GridViewRowEventArgs"/> instance containing the event data.</param>
        protected void gIndividualRecipients_RowDataBound( object sender, GridViewRowEventArgs e )
        {
            var recipientPerson = e.Row.DataItem as Person;
            var lRecipientAlert = e.Row.FindControl( "lRecipientAlert" ) as Literal;
            var lRecipientAlertEmail = e.Row.FindControl( "lRecipientAlertEmail" ) as Literal;
            var lRecipientAlertSMS = e.Row.FindControl( "lRecipientAlertSMS" ) as Literal;
            if ( recipientPerson != null && lRecipientAlert != null )
            {
                string alertClass = string.Empty;
                string alertMessage = string.Empty;
                string alertClassEmail = string.Empty;
                string alertMessageEmail = recipientPerson.Email;
                string alertClassSMS = string.Empty;
                string alertMessageSMS = string.Format( "{0}", recipientPerson.PhoneNumbers.FirstOrDefault( a => a.IsMessagingEnabled ) );

                // General alert info about recipient
                if ( recipientPerson.IsDeceased )
                {
                    alertClass = "text-danger";
                    alertMessage = "Deceased";
                }

                // Email related
                if ( string.IsNullOrWhiteSpace( recipientPerson.Email ) )
                {
                    alertClassEmail = "text-danger";
                    alertMessageEmail = "No Email." + recipientPerson.EmailNote;
                }
                else if ( !recipientPerson.IsEmailActive )
                {
                    // if email is not active, show reason why as tooltip
                    alertClassEmail = "text-danger";
                    alertMessageEmail = "Email is Inactive. " + recipientPerson.EmailNote;
                }
                else
                {
                    // Email is active
                    if ( recipientPerson.EmailPreference != EmailPreference.EmailAllowed )
                    {
                        alertMessageEmail = string.Format( "{0} <span class='label label-warning'>{1}</span>", recipientPerson.Email, recipientPerson.EmailPreference.ConvertToString( true ) );
                        if ( recipientPerson.EmailPreference == EmailPreference.NoMassEmails )
                        {
                            alertClassEmail = "js-no-bulk-email";
                            if ( tglBulkCommunication.Checked )
                            {
                                // This is a bulk email and user does not want bulk emails
                                alertClassEmail += " text-danger";
                            }
                        }
                        else
                        {
                            // Email preference is 'Do Not Email'
                            alertClassEmail = "text-danger";
                        }
                    }
                }

                // SMS Related
                if ( !recipientPerson.PhoneNumbers.Any( a => a.IsMessagingEnabled ) )
                {
                    // No SMS Number
                    alertClassSMS = "text-danger";
                    alertMessageSMS = "No phone number with SMS enabled.";
                }

                lRecipientAlert.Text = string.Format( "<span class=\"{0}\">{1}</span>", alertClass, alertMessage );
                lRecipientAlertEmail.Text = string.Format( "<span class=\"{0}\">{1}</span>", alertClassEmail, alertMessageEmail );
                lRecipientAlertSMS.Text = string.Format( "<span class=\"{0}\">{1}</span>", alertClassSMS, alertMessageSMS );
            }
        }

        /// <summary>
        /// Binds the individual recipients grid.
        /// </summary>
        private void BindIndividualRecipientsGrid()
        {
            var rockContext = new RockContext();
            var personService = new PersonService( rockContext );
            var qryPersons = personService.GetByIds( this.IndividualRecipientPersonIds ).Include( a => a.PhoneNumbers ).OrderBy( a => a.LastName ).ThenBy( a => a.NickName );

            gIndividualRecipients.SetLinqDataSource( qryPersons );
            gIndividualRecipients.DataBind();
        }

        /// <summary>
        /// Handles the DeleteClick event of the gIndividualRecipients control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="RowEventArgs"/> instance containing the event data.</param>
        protected void gIndividualRecipients_DeleteClick( object sender, RowEventArgs e )
        {
            this.IndividualRecipientPersonIds.Remove( e.RowKeyId );
            BindIndividualRecipientsGrid();
            UpdateIndividualRecipientsCountText();

            // upnlContent has UpdateMode = Conditional and this is a modal, so we have to update manually
            upnlContent.Update();
        }

        /// <summary>
        /// Handles the SelectedIndexChanged event of the cblCommunicationGroupSegments control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void cblCommunicationGroupSegments_SelectedIndexChanged( object sender, EventArgs e )
        {
            UpdateRecipientFromListCount();
        }

        /// <summary>
        /// Handles the SelectedIndexChanged event of the ddlCommunicationGroupList control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void ddlCommunicationGroupList_SelectedIndexChanged( object sender, EventArgs e )
        {
            // reload segments in case the communication list has additional segments
            LoadCommunicationSegmentFilters();

            UpdateRecipientFromListCount();
        }

        /// <summary>
        /// Handles the SelectedIndexChanged event of the rblCommunicationGroupSegmentFilterType control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void rblCommunicationGroupSegmentFilterType_SelectedIndexChanged( object sender, EventArgs e )
        {
            UpdateRecipientFromListCount();
        }

        /// <summary>
        /// Updates the recipient from list count.
        /// </summary>
        private void UpdateRecipientFromListCount()
        {
            IQueryable<GroupMember> groupMemberQuery = GetRecipientFromListSelection();

            if ( groupMemberQuery != null )
            {
                int groupMemberCount = groupMemberQuery.Count();
                pnlRecipientFromListCount.Visible = true;

                lRecipientFromListCount.Text = string.Format( "{0} {1} selected", groupMemberCount, "recipient".PluralizeIf( groupMemberCount != 1 ) );
            }
            else
            {
                pnlRecipientFromListCount.Visible = false;
            }
        }

        /// <summary>
        /// Gets the GroupMember Query for the recipients selected on the 'Select From List' tab
        /// </summary>
        /// <param name="groupMemberQuery">The group member query.</param>
        /// <returns></returns>
        private IQueryable<GroupMember> GetRecipientFromListSelection()
        {
            IQueryable<GroupMember> groupMemberQuery = null;
            var rockContext = new RockContext();
            var groupMemberService = new GroupMemberService( rockContext );
            var personService = new PersonService( rockContext );
            var dataViewService = new DataViewService( rockContext );
            int? communicationGroupId = ddlCommunicationGroupList.SelectedValue.AsIntegerOrNull();
            if ( communicationGroupId.HasValue )
            {
                groupMemberQuery = groupMemberService.Queryable().Where( a => a.GroupId == communicationGroupId.Value && a.GroupMemberStatus == GroupMemberStatus.Active );

                var segmentFilterType = rblCommunicationGroupSegmentFilterType.SelectedValueAsEnum<SegmentCriteria>();
                var segmentDataViewIds = cblCommunicationGroupSegments.Items.OfType<ListItem>().Where( a => a.Selected ).Select( a => a.Value.AsInteger() ).ToList();

                Expression segmentExpression = null;
                ParameterExpression paramExpression = personService.ParameterExpression;
                var segmentDataViewList = dataViewService.GetByIds( segmentDataViewIds ).AsNoTracking().ToList();
                foreach ( var segmentDataView in segmentDataViewList )
                {
                    List<string> errorMessages;

                    var exp = segmentDataView.GetExpression( personService, paramExpression, out errorMessages );
                    if ( exp != null )
                    {
                        if ( segmentExpression == null )
                        {
                            segmentExpression = exp;
                        }
                        else
                        {
                            if ( segmentFilterType == SegmentCriteria.All )
                            {
                                segmentExpression = Expression.AndAlso( segmentExpression, exp );
                            }
                            else
                            {
                                segmentExpression = Expression.OrElse( segmentExpression, exp );
                            }
                        }
                    }
                }

                if ( segmentExpression != null )
                {
                    var personQry = personService.Get( paramExpression, segmentExpression );
                    groupMemberQuery = groupMemberQuery.Where( a => personQry.Any( p => p.Id == a.PersonId ) );
                }
            }

            return groupMemberQuery;
        }

        /// <summary>
        /// Handles the Click event of the btnDeleteSelectedRecipients control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnDeleteSelectedRecipients_Click( object sender, EventArgs e )
        {
            // get the selected personIds
            bool removeAll = false;
            var selectField = gIndividualRecipients.ColumnsOfType<SelectField>().First();
            if ( selectField != null && selectField.HeaderCheckbox != null )
            {
                // if the 'Select All' checkbox in the header is checked, and they haven't unselected anything, then assume they want to remove all recipients
                removeAll = selectField.HeaderCheckbox.Checked && gIndividualRecipients.SelectedKeys.Count == gIndividualRecipients.PageSize;
            }

            if ( removeAll )
            {
                IndividualRecipientPersonIds.Clear();
            }
            else
            {
                var selectedPersonIds = gIndividualRecipients.SelectedKeys.OfType<int>().ToList();
                IndividualRecipientPersonIds.RemoveAll( a => selectedPersonIds.Contains( a ) );
            }
            
            BindIndividualRecipientsGrid();

            UpdateIndividualRecipientsCountText();

            // upnlContent has UpdateMode = Conditional and this is a modal, so we have to update manually
            upnlContent.Update();
        }

        #endregion Recipient Selection

        #region Communication Delivery, Medium Selection

        /// <summary>
        /// Shows the Communication Delivery, Medium Selection
        /// </summary>
        private void ShowCommunicationDelivery()
        {
            pnlCommunicationDelivery.Visible = true;
            SetNavigationHistory( pnlCommunicationDelivery );

            // Render warnings for any inactive transports (Javascript will hide and show based on Medium Selection)
            var mediumsWithActiveTransports = Rock.Communication.MediumContainer.Instance.Components.Select( a => a.Value.Value ).Where( x => x.Transport != null && x.Transport.IsActive );
            bool smsTransportEnabled = mediumsWithActiveTransports.Any( a => a.EntityType.Guid == Rock.SystemGuid.EntityType.COMMUNICATION_MEDIUM_SMS.AsGuid() );
            bool emailTransportEnabled = mediumsWithActiveTransports.Any( a => a.EntityType.Guid == Rock.SystemGuid.EntityType.COMMUNICATION_MEDIUM_EMAIL.AsGuid() );

            btnMediumSMS.Visible = smsTransportEnabled;
            btnMediumEmail.Visible = emailTransportEnabled;

            // only prompt for Medium Type if both SMS and Email are available
            rcwMediumType.Visible = smsTransportEnabled && emailTransportEnabled;

            if ( !rcwMediumType.Visible )
            {
                // if only one MediumType is availalbe, automatically set the MediumType to it
                if ( emailTransportEnabled )
                {
                    hfMediumType.Value = CommunicationType.Email.ConvertToInt().ToString();
                }
                else if ( smsTransportEnabled )
                {
                    hfMediumType.Value = CommunicationType.SMS.ConvertToInt().ToString();
                }
            }

            // make sure that either EMAIL or SMS is enabled
            if ( !( emailTransportEnabled || smsTransportEnabled ) )
            {
                nbNoCommunicationTransport.Text = "There are no active Email or SMS communication transports configured.";
                nbNoCommunicationTransport.Visible = true;
                btnCommunicationDeliveryNext.Enabled = false;
            }
            else
            {
                nbNoCommunicationTransport.Visible = false;
                btnCommunicationDeliveryNext.Enabled = true;
            }
        }

        /// <summary>
        /// Handles the Click event of the btnCommunicationDeliveryPrevious control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnCommunicationDeliveryPrevious_Click( object sender, EventArgs e )
        {
            pnlCommunicationDelivery.Visible = false;
            ShowRecipientSelection();
        }

        /// <summary>
        /// Handles the Click event of the btnCommunicationDeliveryNext control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnCommunicationDeliveryNext_Click( object sender, EventArgs e )
        {
            if ( dtpSendDateTime.Visible )
            {
                if ( !dtpSendDateTime.SelectedDateTime.HasValue )
                {
                    nbSendDateTimeWarning.NotificationBoxType = NotificationBoxType.Danger;
                    nbSendDateTimeWarning.Text = "Send Date Time is required";
                    nbSendDateTimeWarning.Visible = true;
                    return;
                }
                else if ( dtpSendDateTime.SelectedDateTime.Value < RockDateTime.Now )
                {
                    nbSendDateTimeWarning.NotificationBoxType = NotificationBoxType.Danger;
                    nbSendDateTimeWarning.Text = "Send Date Time must be immediate or a future date/time";
                    nbSendDateTimeWarning.Visible = true;
                }
            }

            lTitle.Text = tbCommunicationName.Text.FormatAsHtmlTitle();

            // set the confirmation send datetime controls to what we pick here
            tglSendDateTimeConfirmation.Checked = tglSendDateTime.Checked;
            tglSendDateTimeConfirmation_CheckedChanged( null, null );
            dtpSendDateTimeConfirmation.SelectedDateTime = dtpSendDateTime.SelectedDateTime;

            nbSendDateTimeWarning.Visible = false;

            pnlCommunicationDelivery.Visible = false;

            ShowTemplateSelection();
        }

        /// <summary>
        /// Handles the CheckedChanged event of the tglSendDateTime control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void tglSendDateTime_CheckedChanged( object sender, EventArgs e )
        {
            nbSendDateTimeWarning.Visible = false;
            dtpSendDateTime.Visible = !tglSendDateTime.Checked;
        }

        #endregion Communication Delivery, Medium Selection

        #region Template Selection

        /// <summary>
        /// Shows the template selection.
        /// </summary>
        private void ShowTemplateSelection()
        {
            pnlTemplateSelection.Visible = true;
            nbTemplateSelectionWarning.Visible = false;
            SetNavigationHistory( pnlTemplateSelection );
            BindTemplatePicker();
        }

        /// <summary>
        /// Binds the template picker.
        /// </summary>
        private void BindTemplatePicker()
        {
            var rockContext = new RockContext();

            var templateQuery = new CommunicationTemplateService( rockContext ).Queryable().Where( a => a.IsActive );

            int? categoryId = cpCommunicationTemplate.SelectedValue.AsIntegerOrNull();
            if (categoryId.HasValue && categoryId > 0)
            {
                templateQuery = templateQuery.Where( a => a.CategoryId == categoryId );
            }

            templateQuery = templateQuery.OrderBy( a => a.Name );

            // get list of templates that the current user is authorized to View
            IEnumerable<CommunicationTemplate> templateList = templateQuery.AsNoTracking().ToList().Where( a => a.IsAuthorized( Rock.Security.Authorization.VIEW, this.CurrentPerson ) ).ToList();

            // If this is an Email (or RecipientPreference) communication, limit to templates that support the email wizard
            Rock.Model.CommunicationType communicationType = ( Rock.Model.CommunicationType ) hfMediumType.Value.AsInteger();
            if ( communicationType == CommunicationType.Email || communicationType == CommunicationType.RecipientPreference )
            {
                templateList = templateList.Where( a => a.SupportsEmailWizard() );
            }
            else
            {
                templateList = templateList.Where( a => a.HasSMSTemplate() || a.Guid == Rock.SystemGuid.Communication.COMMUNICATION_TEMPLATE_BLANK.AsGuid() );
            }

            rptSelectTemplate.DataSource = templateList;
            rptSelectTemplate.DataBind();
        }

        /// <summary>
        /// Handles the ItemDataBound event of the rptSelectTemplate control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="RepeaterItemEventArgs"/> instance containing the event data.</param>
        protected void rptSelectTemplate_ItemDataBound( object sender, RepeaterItemEventArgs e )
        {
            CommunicationTemplate communicationTemplate = e.Item.DataItem as CommunicationTemplate;

            if ( communicationTemplate != null )
            {
                Literal lTemplateImagePreview = e.Item.FindControl( "lTemplateImagePreview" ) as Literal;
                Literal lTemplateName = e.Item.FindControl( "lTemplateName" ) as Literal;
                Literal lTemplateDescription = e.Item.FindControl( "lTemplateDescription" ) as Literal;
                LinkButton btnSelectTemplate = e.Item.FindControl( "btnSelectTemplate" ) as LinkButton;

                if ( communicationTemplate.ImageFileId.HasValue )
                {
                    var imageUrl = string.Format( "~/GetImage.ashx?id={0}", communicationTemplate.ImageFileId );
                    lTemplateImagePreview.Text = string.Format( "<img src='{0}' width='100%'/>", this.ResolveRockUrl( imageUrl ) );
                }
                else
                {
                    lTemplateImagePreview.Text = string.Format( "<img src='{0}'/>", this.ResolveRockUrl( "~/Assets/Images/communication-template-default.svg" ) );
                }

                lTemplateName.Text = communicationTemplate.Name;
                lTemplateDescription.Text = communicationTemplate.Description;
                btnSelectTemplate.CommandName = "CommunicationTemplateId";
                btnSelectTemplate.CommandArgument = communicationTemplate.Id.ToString();

                if ( hfSelectedCommunicationTemplateId.Value == communicationTemplate.Id.ToString() )
                {
                    btnSelectTemplate.AddCssClass( "template-selected" );
                }
                else
                {
                    btnSelectTemplate.RemoveCssClass( "template-selected" );
                }
            }
        }

        /// <summary>
        /// Handles the Click event of the btnSelectTemplate control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnSelectTemplate_Click( object sender, EventArgs e )
        {
            hfSelectedCommunicationTemplateId.Value = ( sender as LinkButton ).CommandArgument;

            var communicationTemplate = new CommunicationTemplateService( new RockContext() ).Get( hfSelectedCommunicationTemplateId.Value.AsInteger() );

            // If the template does not provide a default From Name and Address use the current person.
            if ( communicationTemplate.FromName.IsNotNullOrWhitespace() )
            {
                tbFromName.Text = communicationTemplate.FromName;
            }
            else
            {
                tbFromName.Text = CurrentPerson.FullName;
            }

            if ( communicationTemplate.FromEmail.IsNotNullOrWhitespace() )
            {
                ebFromAddress.Text = communicationTemplate.FromEmail;
            }
            else
            {
                ebFromAddress.Text = CurrentPerson.Email;
            }

            ebReplyToAddress.Text = communicationTemplate.ReplyToEmail;
            ebCCList.Text = communicationTemplate.CCEmails;
            ebBCCList.Text = communicationTemplate.BCCEmails;

            hfShowAdditionalFields.Value = ( !string.IsNullOrEmpty( communicationTemplate.ReplyToEmail ) || !string.IsNullOrEmpty( communicationTemplate.CCEmails ) || !string.IsNullOrEmpty( communicationTemplate.BCCEmails ) ).ToTrueFalse().ToLower();

            tbEmailSubject.Text = communicationTemplate.Subject;

            hfEmailEditorHtml.Value = communicationTemplate.Message;

            hfEmailAttachedBinaryFileIds.Value = communicationTemplate.GetAttachments( CommunicationType.Email ).Select( a => a.BinaryFileId ).ToList().AsDelimited( "," );
            UpdateEmailAttachedFiles( false );

            // SMS Fields
            if ( communicationTemplate.SMSFromDefinedValueId.HasValue )
            {
                ddlSMSFrom.SetValue( communicationTemplate.SMSFromDefinedValueId.Value );
            }

            tbSMSTextMessage.Text = communicationTemplate.SMSMessage;

            btnTemplateSelectionNext_Click( sender, e );
        }

        /// <summary>
        /// Handles the Click event of the btnTemplateSelectionPrevious control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnTemplateSelectionPrevious_Click( object sender, EventArgs e )
        {
            pnlTemplateSelection.Visible = false;

            ShowCommunicationDelivery();
        }

        /// <summary>
        /// Handles the Click event of the btnTemplateSelectionNext control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnTemplateSelectionNext_Click( object sender, EventArgs e )
        {
            if ( !hfSelectedCommunicationTemplateId.Value.AsIntegerOrNull().HasValue )
            {
                nbTemplateSelectionWarning.Text = "Please select a template.";
                nbTemplateSelectionWarning.Visible = true;
                BindTemplatePicker();
                return;
            }

            nbTemplateSelectionWarning.Visible = false;

            pnlTemplateSelection.Visible = false;

            // The next page should be ShowEmailSummary since this is the Select Email Template Page, but just in case...
            Rock.Model.CommunicationType communicationType = ( Rock.Model.CommunicationType ) hfMediumType.Value.AsInteger();
            if ( communicationType == CommunicationType.Email || communicationType == CommunicationType.RecipientPreference )
            {
                ShowEmailSummary();
            }
            else if ( communicationType == CommunicationType.SMS )
            {
                ShowMobileTextEditor();
            }
        }

        /// <summary>
        /// Handles the SelectItem event of the cpCommunicationTemplate control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void cpCommunicationTemplate_SelectItem( object sender, EventArgs e )
        {
            BindTemplatePicker();
        }

        #endregion Template Selection

        #region Email Editor

        /// <summary>
        /// Shows the email editor.
        /// </summary>
        private void ShowEmailEditor()
        {
            tbTestEmailAddress.Text = this.CurrentPerson.Email;

            ifEmailDesigner.Attributes["srcdoc"] = hfEmailEditorHtml.Value;
            pnlEmailEditor.Visible = true;
            SetNavigationHistory( pnlEmailEditor );
        }

        /// <summary>
        /// Handles the Click event of the btnEmailEditorPrevious control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnEmailEditorPrevious_Click( object sender, EventArgs e )
        {
            pnlEmailEditor.Visible = false;
            ShowEmailSummary();
            
        }

        /// <summary>
        /// Handles the Click event of the btnEmailEditorNext control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnEmailEditorNext_Click( object sender, EventArgs e )
        {
            ifEmailDesigner.Attributes["srcdoc"] = hfEmailEditorHtml.Value;
            pnlEmailEditor.Visible = false;

            Rock.Model.CommunicationType communicationType = ( Rock.Model.CommunicationType ) hfMediumType.Value.AsInteger();
            if ( communicationType == CommunicationType.SMS || communicationType == CommunicationType.RecipientPreference )
            {
                ShowMobileTextEditor();
            }
            else
            {
                ShowConfirmation();
            }
        }

        /// <summary>
        /// Handles the Click event of the btnEmailSendTest control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnEmailSendTest_Click( object sender, EventArgs e )
        {
            SendTestCommunication( EntityTypeCache.Read( Rock.SystemGuid.EntityType.COMMUNICATION_MEDIUM_EMAIL.AsGuid() ).Id, nbEmailTestResult );

            // make sure the email designer keeps the html source that was there
            ifEmailDesigner.Attributes["srcdoc"] = hfEmailEditorHtml.Value;

            // upnlContent has UpdateMode = Conditional, so we have to update manually
            upnlContent.Update();
        }

        /// <summary>
        /// Sends the test communication.
        /// </summary>
        /// <param name="mediumEntityTypeId">The medium entity type identifier.</param>
        private void SendTestCommunication( int mediumEntityTypeId, NotificationBox nbResult )
        {
            if ( !ValidateSendDateTime() )
            {
                return;
            }

            try
            {
                CleanupOrphanedTestCommunications();
            }
            catch( Exception ex)
            {
                this.LogException( ex );
            }

            Rock.Model.Communication communication = UpdateCommunication( new RockContext() );

            if ( communication != null )
            {
                using ( var rockContext = new RockContext() )
                {
                    // Using a new context (so that changes in the UpdateCommunication() are not persisted )
                    int? testPersonId = null;
                    Rock.Model.Communication testCommunication = null;
                    CommunicationService communicationService = null;

                    try
                    {
                        testCommunication = communication.Clone( false );
                        testCommunication.Id = 0;
                        testCommunication.Guid = Guid.Empty;
                        testCommunication.EnabledLavaCommands = GetAttributeValue( "EnabledLavaCommands" );
                        testCommunication.ForeignGuid = null;
                        testCommunication.ForeignId = null;
                        testCommunication.ForeignKey = null;

                        testCommunication.FutureSendDateTime = null;
                        testCommunication.Status = CommunicationStatus.Approved;
                        testCommunication.ReviewedDateTime = RockDateTime.Now;
                        testCommunication.ReviewerPersonAliasId = CurrentPersonAliasId;
                        foreach ( var attachment in communication.Attachments )
                        {
                            var cloneAttachment = attachment.Clone( false );
                            cloneAttachment.Id = 0;
                            cloneAttachment.Guid = Guid.Empty;
                            cloneAttachment.ForeignGuid = null;
                            cloneAttachment.ForeignId = null;
                            cloneAttachment.ForeignKey = null;

                            testCommunication.Attachments.Add( cloneAttachment );
                        }

                        // for the test email, just use the current person as the recipient, but copy/paste the AdditionalMergeValuesJson to our test recipient so it has the same as the real recipients
                        var testRecipient = new CommunicationRecipient();
                        if ( communication.GetRecipientCount( rockContext ) > 0 )
                        {
                            var recipient = communication.GetRecipientsQry( rockContext ).FirstOrDefault();
                            testRecipient.AdditionalMergeValuesJson = recipient.AdditionalMergeValuesJson;
                        }

                        testRecipient.Status = CommunicationRecipientStatus.Pending;
                        testRecipient.PersonAliasId = CurrentPersonAliasId.Value;

                        var testPerson = CurrentPerson.Clone( false );
                        testPerson.Id = 0;
                        testPerson.Guid = Guid.NewGuid();
                        if ( mediumEntityTypeId == EntityTypeCache.Read( Rock.SystemGuid.EntityType.COMMUNICATION_MEDIUM_EMAIL.AsGuid() ).Id )
                        {
                            testPerson.Email = tbTestEmailAddress.Text;
                        }
                        else if ( mediumEntityTypeId == EntityTypeCache.Read( Rock.SystemGuid.EntityType.COMMUNICATION_MEDIUM_SMS.AsGuid() ).Id )
                        {
                            testPerson.PhoneNumbers = new List<Rock.Model.PhoneNumber>();
                            testPerson.PhoneNumbers.Add( new PhoneNumber { IsMessagingEnabled = true, Number = tbTestSMSNumber.Text } );
                        }

                        testPerson.ForeignGuid = null;
                        testPerson.ForeignId = null;

                        // Just in case it doesn't get cleaned up, mark it so that we can try to clean it up next time
                        testPerson.ForeignKey = "_ForTestCommunication_";

                        var personService = new PersonService( rockContext );
                        personService.Add( testPerson );
                        rockContext.SaveChanges();
                        testPersonId = testPerson.Id;
                        testPerson = personService.Get( testPersonId.Value );
                        testRecipient.PersonAliasId = testPerson.PrimaryAliasId.Value;

                        testRecipient.MediumEntityTypeId = mediumEntityTypeId;

                        // If we are just sending a Test Email, don't use set it up to use the CommunicationList
                        testCommunication.ListGroup = null;
                        testCommunication.ListGroupId = null;

                        testCommunication.Recipients.Add( testRecipient );

                        communicationService = new CommunicationService( rockContext );
                        communicationService.Add( testCommunication );
                        rockContext.SaveChanges();

                        foreach ( var medium in testCommunication.Mediums )
                        {
                            medium.Send( testCommunication );
                        }

                        testRecipient = new CommunicationRecipientService( rockContext )
                            .Queryable().AsNoTracking()
                            .Where( r => r.CommunicationId == testCommunication.Id )
                            .FirstOrDefault();

                        if ( testRecipient != null && testRecipient.Status == CommunicationRecipientStatus.Failed && testRecipient.PersonAlias != null && testRecipient.PersonAlias.Person != null )
                        {
                            nbResult.NotificationBoxType = NotificationBoxType.Danger;
                            nbResult.Text = string.Format( "Test communication to <strong>{0}</strong> failed: {1}.", testRecipient.PersonAlias.Person.FullName, testRecipient.StatusNote );
                        }
                        else
                        {
                            nbResult.NotificationBoxType = NotificationBoxType.Success;
                            nbResult.Text = "Test communication has been sent.";
                        }

                        nbResult.Dismissable = true;
                        nbResult.Visible = true;
                    }
                    finally
                    {
                        // make sure we delete the test communication record we created to send the test 
                        if ( communicationService != null && testCommunication != null )
                        {
                            communicationService.Delete( testCommunication );
                            rockContext.SaveChanges();
                        }

                        // make sure we delete the Person record we created to send the test 
                        using ( var deleteRockContext = new RockContext() )
                        {
                            var personToDeleteService = new PersonService( deleteRockContext );
                            var personAliasToDeleteService = new PersonAliasService( deleteRockContext );

                            if ( testPersonId.HasValue )
                            {
                                var personToDelete = personToDeleteService.Get( testPersonId.Value );
                                personAliasToDeleteService.DeleteRange( personToDelete.Aliases );
                                personToDelete.Aliases = null;
                                personToDeleteService.Delete( personToDelete );
                                deleteRockContext.SaveChanges();
                            }
}
                    }
                }
            }
        }

        /// <summary>
        /// Shouldn't happen, but just in case...
        /// this will cleanup any orphaned test communications artifacts
        /// </summary>
        private void CleanupOrphanedTestCommunications()
        {
            using ( RockContext deleteRockContext = new RockContext() )
            {
                PersonService personToDeleteService = new PersonService( deleteRockContext );
                PersonAliasService personAliasToDeleteService = new PersonAliasService( deleteRockContext );
                
                // check for any test communication artifacts that didn't clean up correctly
                var forTestCommunicationPersonQry = personToDeleteService.Queryable().Where( a => a.ForeignKey == "_ForTestCommunication_" );
                if ( forTestCommunicationPersonQry.Any() )
                {
                    var communicationToDeleteService = new Rock.Model.CommunicationService( deleteRockContext );
                    var communicationToDeleteQry = communicationToDeleteService.Queryable().Where( a => a.Recipients.Any( r => forTestCommunicationPersonQry.Any( p => p.Id == r.PersonAlias.PersonId ) ) );
                    foreach ( var testCommunicationToDelete in communicationToDeleteQry.ToList() )
                    {
                        communicationToDeleteService.Delete( testCommunicationToDelete );
                    }

                    foreach ( var personToDelete in forTestCommunicationPersonQry )
                    {
                        foreach ( var personToDeleteAlias in personToDelete.Aliases.ToList() )
                        {
                            personAliasToDeleteService.Delete( personToDeleteAlias );
                        }

                        personToDeleteService.Delete( personToDelete );
                    }
                }
            }
        }

        /// <summary>
        /// Handles the Click event of the btnEmailPreview control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnEmailPreview_Click( object sender, EventArgs e )
        {
            // make sure the ifEmailDesigner srcdoc gets updated from what was in the email editor
            ifEmailDesigner.Attributes["srcdoc"] = hfEmailEditorHtml.Value;

            upnlEmailPreview.Update();

            var rockContext = new RockContext();
            var communication = UpdateCommunication( rockContext );
            Person currentPerson;
            if ( communication.CreatedByPersonAlias != null && communication.CreatedByPersonAlias.Person != null )
            {
                currentPerson = communication.CreatedByPersonAlias.Person;
            }
            else
            {
                currentPerson = this.CurrentPerson;
            }

            var commonMergeFields = Rock.Lava.LavaHelper.GetCommonMergeFields( null, currentPerson );
            var sampleCommunicationRecipient = communication.Recipients.FirstOrDefault();
            sampleCommunicationRecipient.Communication = communication;
            sampleCommunicationRecipient.PersonAlias = sampleCommunicationRecipient.PersonAlias ?? new PersonAliasService( rockContext ).Get( sampleCommunicationRecipient.PersonAliasId );
            var mergeFields = sampleCommunicationRecipient.CommunicationMergeValues( commonMergeFields );

            Rock.Communication.MediumComponent emailMediumWithActiveTransport = Rock.Communication.MediumContainer.Instance.Components.Select( a => a.Value.Value )
                .Where( x => x.Transport != null && x.Transport.IsActive )
                .Where( a => a.EntityType.Guid == Rock.SystemGuid.EntityType.COMMUNICATION_MEDIUM_EMAIL.AsGuid() ).FirstOrDefault();

            string communicationHtml = hfEmailEditorHtml.Value;
            
            if ( emailMediumWithActiveTransport != null )
            {
                var mediumAttributes = new Dictionary<string, string>();
                foreach ( var attr in emailMediumWithActiveTransport.Attributes.Select( a => a.Value ) )
                {
                    string value = emailMediumWithActiveTransport.GetAttributeValue( attr.Key );
                    if ( value.IsNotNullOrWhitespace() )
                    {
                        mediumAttributes.Add( attr.Key, value );
                    }
                }

                string publicAppRoot = GlobalAttributesCache.Read().GetValue( "PublicApplicationRoot" ).EnsureTrailingForwardslash();

                // Add Html view
                // Get the unsubscribe content and add a merge field for it
                if ( communication.IsBulkCommunication && mediumAttributes.ContainsKey( "UnsubscribeHTML" ) )
                {
                    string unsubscribeHtml = emailMediumWithActiveTransport.Transport.ResolveText( mediumAttributes["UnsubscribeHTML"], currentPerson, communication.EnabledLavaCommands, mergeFields, publicAppRoot );
                    mergeFields.AddOrReplace( "UnsubscribeOption", unsubscribeHtml );
                    communicationHtml = emailMediumWithActiveTransport.Transport.ResolveText( communicationHtml, currentPerson, communication.EnabledLavaCommands, mergeFields, publicAppRoot );

                    // Resolve special syntax needed if option was included in global attribute
                    if ( Regex.IsMatch( communicationHtml, @"\[\[\s*UnsubscribeOption\s*\]\]" ) )
                    {
                        communicationHtml = Regex.Replace( communicationHtml, @"\[\[\s*UnsubscribeOption\s*\]\]", unsubscribeHtml );
                    }

                    // Add the unsubscribe option at end if it wasn't included in content
                    if ( !communicationHtml.Contains( unsubscribeHtml ) )
                    {
                        communicationHtml += unsubscribeHtml;
                    }
                }
                else
                {
                    communicationHtml = emailMediumWithActiveTransport.Transport.ResolveText( communicationHtml, currentPerson, communication.EnabledLavaCommands, mergeFields, publicAppRoot );
                    communicationHtml = Regex.Replace( communicationHtml, @"\[\[\s*UnsubscribeOption\s*\]\]", string.Empty );
                }
            }

            ifEmailPreview.Attributes["srcdoc"] = communicationHtml;

            pnlEmailPreview.Visible = true;
            mdEmailPreview.Show();
        }

        #endregion Email Editor

        #region Email Summary

        /// <summary>
        /// Shows the email summary.
        /// </summary>
        private void ShowEmailSummary()
        {
            // See if the template supports preview-text
            HtmlAgilityPack.HtmlDocument templateDoc = new HtmlAgilityPack.HtmlDocument();
            templateDoc.LoadHtml( hfEmailEditorHtml.Value );
            var preheaderTextNode = templateDoc.GetElementbyId( "preheader-text" );
            tbEmailPreview.Visible = preheaderTextNode != null;
            tbEmailPreview.Text = preheaderTextNode != null ? preheaderTextNode.InnerHtml : string.Empty;

            pnlEmailSummary.Visible = true;
            SetNavigationHistory( pnlEmailSummary );
        }

        /// <summary>
        /// Handles the Click event of the btnEmailSummaryPrevious control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnEmailSummaryPrevious_Click( object sender, EventArgs e )
        {
            pnlTemplateSelection.Visible = true;
            BindTemplatePicker();

            pnlEmailSummary.Visible = false;
        }

        /// <summary>
        /// Handles the Click event of the btnEmailSummaryNext control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnEmailSummaryNext_Click( object sender, EventArgs e )
        {
            if ( tbEmailPreview.Visible )
            {
                // set the preheader-text of our email html
                HtmlAgilityPack.HtmlDocument templateDoc = new HtmlAgilityPack.HtmlDocument();
                templateDoc.LoadHtml( hfEmailEditorHtml.Value );
                var preheaderTextNode = templateDoc.GetElementbyId( "preheader-text" );
                if ( preheaderTextNode != null )
                {
                    preheaderTextNode.InnerHtml = tbEmailPreview.Text;
                    hfEmailEditorHtml.Value = templateDoc.DocumentNode.OuterHtml;
                }

                tbEmailPreview.Text = preheaderTextNode != null ? preheaderTextNode.InnerHtml : string.Empty;
            }

            pnlEmailSummary.Visible = false;

            ShowEmailEditor();
        }

        /// <summary>
        /// Handles the FileUploaded event of the fupEmailAttachments control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="FileUploaderEventArgs"/> instance containing the event data.</param>
        protected void fupEmailAttachments_FileUploaded( object sender, FileUploaderEventArgs e )
        {
            UpdateEmailAttachedFiles( true );
        }

        /// <summary>
        /// Updates the HTML for the Attached Files and optionally adds the file that was just uploaded using the file uploader
        /// </summary>
        private void UpdateEmailAttachedFiles( bool addUploadedFile )
        {
            List<int> attachmentList = hfEmailAttachedBinaryFileIds.Value.SplitDelimitedValues().AsIntegerList();
            if ( addUploadedFile && fupEmailAttachments.BinaryFileId.HasValue )
            {
                if ( !attachmentList.Contains( fupEmailAttachments.BinaryFileId.Value ) )
                {
                    attachmentList.Add( fupEmailAttachments.BinaryFileId.Value );
                }
            }

            hfEmailAttachedBinaryFileIds.Value = attachmentList.AsDelimited( "," );
            fupEmailAttachments.BinaryFileId = null;

            // pre-populate dictionary so that the attachments are listed in the order they were added
            Dictionary<int, string> binaryFileAttachments = attachmentList.ToDictionary( k => k, v => string.Empty );
            using ( var rockContext = new RockContext() )
            {
                var binaryFileInfoList = new BinaryFileService( rockContext ).Queryable()
                        .Where( f => attachmentList.Contains( f.Id ) )
                        .Select( f => new
                        {
                            f.Id,
                            f.FileName
                        } );

                foreach ( var binaryFileInfo in binaryFileInfoList )
                {
                    binaryFileAttachments[binaryFileInfo.Id] = binaryFileInfo.FileName;
                }
            }

            StringBuilder sbAttachmentsHtml = new StringBuilder();
            sbAttachmentsHtml.AppendLine( "<div class='attachment'>" );
            sbAttachmentsHtml.AppendLine( "  <ul class='attachment-content'>" );

            foreach ( var binaryFileAttachment in binaryFileAttachments )
            {
                var attachmentUrl = string.Format( "{0}GetFile.ashx?id={1}", System.Web.VirtualPathUtility.ToAbsolute( "~" ), binaryFileAttachment.Key );
                var removeAttachmentJS = string.Format( "removeAttachment( this, '{0}', '{1}' );", hfEmailAttachedBinaryFileIds.ClientID, binaryFileAttachment.Key );
                sbAttachmentsHtml.AppendLine( string.Format( "    <li><a href='{0}' target='_blank'>{1}</a> <a><i class='fa fa-times' onclick=\"{2}\"></i></a></li>", attachmentUrl, binaryFileAttachment.Value, removeAttachmentJS ) );
            }

            sbAttachmentsHtml.AppendLine( "  </ul>" );
            sbAttachmentsHtml.AppendLine( "</div>" );

            lEmailAttachmentListHtml.Text = sbAttachmentsHtml.ToString();
        }

        #endregion Email Summary

        #region Mobile Text Editor

        /// <summary>
        /// Shows the mobile text editor.
        /// </summary>
        private void ShowMobileTextEditor()
        {
            if ( !ddlSMSFrom.SelectedValue.AsIntegerOrNull().HasValue )
            {
                InitializeSMSFromSender( this.CurrentPerson );
            }

            mfpSMSMessage.MergeFields.Clear();
            mfpSMSMessage.MergeFields.Add( "GlobalAttribute" );
            mfpSMSMessage.MergeFields.Add( "Rock.Model.Person" );

            var currentPersonSMSNumber = this.CurrentPerson.PhoneNumbers.FirstOrDefault( a => a.IsMessagingEnabled );
            tbTestSMSNumber.Text = currentPersonSMSNumber != null ? currentPersonSMSNumber.NumberFormatted : string.Empty;

            // make the PersonId of the First Recipient available to Javascript so that we can do some Lava processing using REST and Javascript
            var rockContext = new RockContext();
            Rock.Model.Communication communication = UpdateCommunication( rockContext );
            var firstRecipient = communication.Recipients.First();
            firstRecipient.PersonAlias = firstRecipient.PersonAlias ?? new PersonAliasService( rockContext ).Get( firstRecipient.PersonAliasId );
            hfSMSSampleRecipientPersonId.Value = communication.Recipients.First().PersonAlias.PersonId.ToString();

            nbSMSTestResult.Visible = false;
            pnlMobileTextEditor.Visible = true;
            SetNavigationHistory( pnlMobileTextEditor );
        }

        /// <summary>
        /// Handles the FileUploaded event of the fupMobileAttachment control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="FileUploaderEventArgs"/> instance containing the event data.</param>
        protected void fupMobileAttachment_FileUploaded( object sender, FileUploaderEventArgs e )
        {
            UpdateMobileAttachedFiles( true );
        }

        /// <summary>
        /// Handles the FileRemoved event of the fupMobileAttachment control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="FileUploaderEventArgs"/> instance containing the event data.</param>
        protected void fupMobileAttachment_FileRemoved( object sender, FileUploaderEventArgs e )
        {
            // ensure that the binaryfileid is set to null if the file was removed
            fupMobileAttachment.BinaryFileId = null;
            UpdateMobileAttachedFiles( true );
        }

        /// <summary>
        /// Updates the mobile attached files.
        /// </summary>
        /// <param name="resizeImageIfNeeded">if set to <c>true</c> [resize image if needed].</param>
        protected void UpdateMobileAttachedFiles( bool resizeImageIfNeeded )
        {
            nbMobileAttachmentFileTypeWarning.Visible = false;
            nbMobileAttachmentSizeWarning.Visible = false;
            if ( !fupMobileAttachment.BinaryFileId.HasValue )
            {
                imgSMSImageAttachment.Visible = false;
                return;
            }

            var rockContext = new RockContext();
            BinaryFileService binaryFileService = new BinaryFileService( rockContext );

            // load binary file from database
            var binaryFile = binaryFileService.Get( fupMobileAttachment.BinaryFileId.Value );
            if ( binaryFile != null )
            {
                if ( binaryFile.MimeType.StartsWith( "image/", StringComparison.OrdinalIgnoreCase ) )
                {
                    System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap( binaryFile.ContentStream );

                    // intentionally tell imageResizer to ignore the 3200x3200 size limit so that we can crop it first before limiting the size.
                    var sizingPlugin = ImageResizer.Configuration.Config.Current.Plugins.Get<ImageResizer.Plugins.Basic.SizeLimiting>();
                    var origLimit = sizingPlugin.Limits.TotalBehavior;
                    sizingPlugin.Limits.TotalBehavior = ImageResizer.Plugins.Basic.SizeLimits.TotalSizeBehavior.IgnoreLimits;

                    var imageStream = binaryFile.ContentStream;

                    try
                    {

                        // Make sure Image is no bigger than maxwidth
                        int maxWidth = this.GetAttributeValue( "MaxSMSImageWidth" ).AsIntegerOrNull() ?? 600;
                        imageStream.Seek( 0, SeekOrigin.Begin );
                        System.Drawing.Bitmap croppedBitmap = new System.Drawing.Bitmap( imageStream );

                        if ( ( croppedBitmap.Width > maxWidth ) )
                        {
                            string resizeParams = string.Format( "width={0}", maxWidth );
                            MemoryStream croppedAndResizedStream = new MemoryStream();
                            ImageResizer.ResizeSettings resizeSettings = new ImageResizer.ResizeSettings( resizeParams );
                            imageStream.Seek( 0, SeekOrigin.Begin );
                            ImageResizer.ImageBuilder.Current.Build( imageStream, croppedAndResizedStream, resizeSettings );

                            binaryFile.ContentStream = croppedAndResizedStream;

                            rockContext.SaveChanges();
                        }
                    }
                    finally
                    {
                        // set the size limit behavior back to what it was
                        sizingPlugin.Limits.TotalBehavior = origLimit;
                    }

                    string publicAppRoot = GlobalAttributesCache.Read().GetValue( "PublicApplicationRoot" ).EnsureTrailingForwardslash();
                    imgSMSImageAttachment.ImageUrl = string.Format( "{0}GetImage.ashx?guid={1}", publicAppRoot, binaryFile.Guid );
                    divAttachmentLoadError.InnerText = "Unable to load attachment from " + imgSMSImageAttachment.ImageUrl;
                    imgSMSImageAttachment.Visible = true;
                    imgSMSImageAttachment.Width = new Unit( 50, UnitType.Percentage );
                }
                else
                {

                    // get a thumbnail based on the file extension
                    var virtualThumbnailFilePath = "~/Assets/Icons/FileTypes/other.png";
                    if ( !string.IsNullOrEmpty( binaryFile.FileName ) )
                    {
                        string fileExtension = Path.GetExtension( binaryFile.FileName ).TrimStart( '.' );
                        virtualThumbnailFilePath = string.Format( "~/Assets/Icons/FileTypes/{0}.png", fileExtension );
                        string physicalFilePath = HttpContext.Current.Request.MapPath( virtualThumbnailFilePath );
                        if ( !File.Exists( physicalFilePath ) )
                        {
                            virtualThumbnailFilePath = "~/Assets/Icons/FileTypes/other.png";
                        }
                    }

                    string publicAppRoot = GlobalAttributesCache.Read().GetValue( "PublicApplicationRoot" ).EnsureTrailingForwardslash();
                    imgSMSImageAttachment.ImageUrl = virtualThumbnailFilePath.Replace( "~/", publicAppRoot );
                    divAttachmentLoadError.InnerText = "Unable to load preview icon from " + imgSMSImageAttachment.ImageUrl;
                    imgSMSImageAttachment.Visible = true;
                    imgSMSImageAttachment.Width = new Unit( 10, UnitType.Percentage );
                }

                if ( Rock.Communication.Transport.Twilio.SupportedMimeTypes.Any( a => binaryFile.MimeType.Equals( a, StringComparison.OrdinalIgnoreCase ) ) )
                {
                    // fully supported, no warning or info about filetype is needed
                    nbMobileAttachmentFileTypeWarning.Visible = false;
                }
                else if ( Rock.Communication.Transport.Twilio.AcceptedMimeTypes.Any( a => binaryFile.MimeType.Equals( a, StringComparison.OrdinalIgnoreCase ) ) )
                {
                    // accepted, but not fully supported. Show an info message
                    nbMobileAttachmentFileTypeWarning.NotificationBoxType = NotificationBoxType.Info;
                    nbMobileAttachmentFileTypeWarning.Text = string.Format( "When sending attachments with MMS; jpg, gif, and png images are supported for all carriers. Files of type <small>{0}</small> are also accepted, but support is dependent upon each carrier and device. So make sure to test sending this to different carriers and phone types to see if it will work as expected.", binaryFile.MimeType );
                    nbMobileAttachmentFileTypeWarning.Visible = true;
                }
                else
                {
                    // might not be accepted, and definitely not fully supported. Show a warning message
                    nbMobileAttachmentFileTypeWarning.Text = string.Format( "When sending attachments with MMS; jpg, gif, and png images are supported for all carriers. However, files of type <small>{0}</small> might not be accepted, and support is dependent upon each carrier and device. So make sure to test sending this to different carriers and phone types to see if it will work as expected.", binaryFile.MimeType );
                    nbMobileAttachmentFileTypeWarning.NotificationBoxType = NotificationBoxType.Warning;
                    nbMobileAttachmentFileTypeWarning.Visible = true;
                }

                if ( binaryFile.FileSize > Rock.Communication.Transport.Twilio.MediaSizeLimitBytes )
                {
                    // bigger than what twilio says it supports
                    nbMobileAttachmentSizeWarning.Visible = true;
                    nbMobileAttachmentSizeWarning.Text = string.Format( "The attached file is {0}MB, which is over the {1}MB media size limit.", binaryFile.FileSize / 1024 / 1024, ( Rock.Communication.Transport.Twilio.MediaSizeLimitBytes / 1024 / 1024 ) );
                }

                upnlContent.Update();
            }
        }

        /// <summary>
        /// Handles the Click event of the btnMobileTextEditorPrevious control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnMobileTextEditorPrevious_Click( object sender, EventArgs e )
        {
            pnlMobileTextEditor.Visible = false;

            Rock.Model.CommunicationType communicationType = ( Rock.Model.CommunicationType ) hfMediumType.Value.AsInteger();
            if ( communicationType == CommunicationType.Email || communicationType == CommunicationType.RecipientPreference )
            {
                ShowEmailEditor();
            }
            else
            {
                ShowTemplateSelection();
            }
        }

        /// <summary>
        /// Handles the Click event of the btnMobileTextEditorNext control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnMobileTextEditorNext_Click( object sender, EventArgs e )
        {
            pnlMobileTextEditor.Visible = false;
            ShowConfirmation();
        }

        /// <summary>
        /// Initializes the SMS from sender.
        /// </summary>
        /// <param name="sender">The sender.</param>
        public void InitializeSMSFromSender( Person sender )
        {
            var numbers = DefinedTypeCache.Read( Rock.SystemGuid.DefinedType.COMMUNICATION_SMS_FROM.AsGuid() );
            if ( numbers != null )
            {
                foreach ( var number in numbers.DefinedValues )
                {
                    var personAliasGuid = number.GetAttributeValue( "ResponseRecipient" ).AsGuidOrNull();
                    if ( personAliasGuid.HasValue && sender.Aliases.Any( a => a.Guid == personAliasGuid.Value ) )
                    {
                        ddlSMSFrom.SetValue( number.Id );
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Handles the SelectItem event of the mfpMessage control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void mfpMessage_SelectItem( object sender, EventArgs e )
        {
            tbSMSTextMessage.Text += mfpSMSMessage.SelectedMergeField;
            mfpSMSMessage.SetValue( string.Empty );
        }

        /// <summary>
        /// Handles the Click event of the btnSMSSendTest control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnSMSSendTest_Click( object sender, EventArgs e )
        {
            if (ddlSMSFrom.SelectedValue.IsNullOrWhiteSpace())
            {
                nbSMSTestResult.NotificationBoxType = NotificationBoxType.Danger;
                nbSMSTestResult.Text = "A 'From' number must be specified.";
                nbSMSTestResult.Dismissable = true;
                nbSMSTestResult.Visible = true;
                return;
            }

            SendTestCommunication( EntityTypeCache.Read( Rock.SystemGuid.EntityType.COMMUNICATION_MEDIUM_SMS.AsGuid() ).Id, nbSMSTestResult );
        }

        #endregion Mobile Text Editor

        #region Confirmation

        /// <summary>
        /// Shows the confirmation.
        /// </summary>
        private void ShowConfirmation()
        {
            pnlConfirmation.Visible = true;
            SetNavigationHistory( pnlConfirmation );

            string sendDateTimeText = string.Empty;
            if ( tglSendDateTimeConfirmation.Checked )
            {
                sendDateTimeText = "immediately";
            }
            else if ( dtpSendDateTimeConfirmation.SelectedDateTime.HasValue )
            {
                sendDateTimeText = dtpSendDateTimeConfirmation.SelectedDateTime.Value.ToString( "f" );
            }

            lConfirmationSendDateTimeHtml.Text = string.Format( "This communication has been configured to send {0}.", sendDateTimeText );

            int sendCount;
            string sendCountTerm = "individual";

            if ( tglRecipientSelection.Checked )
            {
                sendCount = this.GetRecipientFromListSelection().Count();
            }
            else
            {
                sendCount = this.IndividualRecipientPersonIds.Count();
            }

            lblConfirmationSendHtml.Text = string.Format(
@"<p>Now Is the Moment Of Truth</p>
<p>You are about to send this communication to <strong>{0}</strong> {1}</p>
",
sendCount,
sendCountTerm.PluralizeIf( sendCount != 1 ) );

            int maxRecipients = GetAttributeValue( "MaximumRecipients" ).AsIntegerOrNull() ?? int.MaxValue;
            bool userCanApprove = IsUserAuthorized( "Approve" );
            if ( sendCount > maxRecipients && !userCanApprove )
            {
                btnSend.Text = "Submit";
            }
            else
            {
                btnSend.Text = "Send";
            }
        }

        /// <summary>
        /// Handles the Click event of the btnConfirmationPrevious control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnConfirmationPrevious_Click( object sender, EventArgs e )
        {
            pnlConfirmation.Visible = false;

            Rock.Model.CommunicationType communicationType = ( Rock.Model.CommunicationType ) hfMediumType.Value.AsInteger();
            if ( communicationType == CommunicationType.SMS || communicationType == CommunicationType.RecipientPreference )
            {
                ShowMobileTextEditor();
            }
            else if ( communicationType == CommunicationType.Email )
            {
                ShowEmailEditor();
            }
        }

        /// <summary>
        /// Handles the Click event of the btnSend control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnSend_Click( object sender, EventArgs e )
        {
            if ( !ValidateSendDateTime() )
            {
                return;
            }

            var rockContext = new RockContext();
            Rock.Model.Communication communication = UpdateCommunication( rockContext );

            int maxRecipients = GetAttributeValue( "MaximumRecipients" ).AsIntegerOrNull() ?? int.MaxValue;
            bool userCanApprove = IsUserAuthorized( "Approve" );
            var recipientCount = communication.GetRecipientCount( rockContext );
            string message = string.Empty;
            if ( recipientCount > maxRecipients && !userCanApprove )
            {
                communication.Status = CommunicationStatus.PendingApproval;
                message = "Communication has been submitted for approval.";
            }
            else
            {
                communication.Status = CommunicationStatus.Approved;
                communication.ReviewedDateTime = RockDateTime.Now;
                communication.ReviewerPersonAliasId = CurrentPersonAliasId;

                if ( communication.FutureSendDateTime.HasValue &&
                               communication.FutureSendDateTime > RockDateTime.Now )
                {
                    message = string.Format(
                        "Communication will be sent {0}.",
                        communication.FutureSendDateTime.Value.ToRelativeDateString( 0 ) );
                }
                else
                {
                    message = "Communication has been queued for sending.";
                }
            }

            rockContext.SaveChanges();

            hfCommunicationId.Value = communication.Id.ToString();

            // send approval email if needed (now that we have a communication id)
            if ( communication.Status == CommunicationStatus.PendingApproval )
            {
                var approvalTransaction = new Rock.Transactions.SendCommunicationApprovalEmail();
                approvalTransaction.CommunicationId = communication.Id;
                approvalTransaction.ApprovalPageUrl = HttpContext.Current.Request.Url.AbsoluteUri;
                Rock.Transactions.RockQueue.TransactionQueue.Enqueue( approvalTransaction );
            }

            if ( communication.Status == CommunicationStatus.Approved &&
                ( !communication.FutureSendDateTime.HasValue || communication.FutureSendDateTime.Value <= RockDateTime.Now ) )
            {
                if ( GetAttributeValue( "SendWhenApproved" ).AsBoolean() )
                {
                    var transaction = new Rock.Transactions.SendCommunicationTransaction();
                    transaction.CommunicationId = communication.Id;
                    transaction.PersonAlias = CurrentPersonAlias;
                    Rock.Transactions.RockQueue.TransactionQueue.Enqueue( transaction );
                }
            }

            ShowResult( message, communication );
        }

        /// <summary>
        /// Handles the Click event of the btnSaveAsDraft control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void btnSaveAsDraft_Click( object sender, EventArgs e )
        {
            if ( !ValidateSendDateTime() )
            {
                return;
            }

            var rockContext = new RockContext();
            Rock.Model.Communication communication = UpdateCommunication( rockContext );
            communication.Status = CommunicationStatus.Draft;
            rockContext.SaveChanges();

            hfCommunicationId.Value = communication.Id.ToString();

            ShowResult( "The communication has been saved", communication );
        }

        /// <summary>
        /// Shows the result.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="communication">The communication.</param>
        private void ShowResult( string message, Rock.Model.Communication communication )
        {
            SetNavigationHistory( pnlResult );
            pnlConfirmation.Visible = false;
            pnlResult.Visible = true;

            nbResult.Text = message;

            CurrentPageReference.Parameters.AddOrReplace( "CommunicationId", communication.Id.ToString() );
            hlViewCommunication.NavigateUrl = CurrentPageReference.BuildUrl();

            // only show the Link if there is a CommunicationDetail block type on this page
            hlViewCommunication.Visible = this.PageCache.Blocks.Any( a => a.BlockType.Guid == Rock.SystemGuid.BlockType.COMMUNICATION_DETAIL.AsGuid() );
        }

        /// <summary>
        /// Displays a message if the send datetime is not valid, and returns true if send datetime is valid
        /// </summary>
        /// <returns></returns>
        private bool ValidateSendDateTime()
        {
            if ( dtpSendDateTimeConfirmation.Visible )
            {
                if ( !dtpSendDateTimeConfirmation.SelectedDateTime.HasValue )
                {
                    nbSendDateTimeWarningConfirmation.NotificationBoxType = NotificationBoxType.Danger;
                    nbSendDateTimeWarningConfirmation.Text = "Send Date Time is required";
                    nbSendDateTimeWarningConfirmation.Visible = true;
                    return false;
                }
                else if ( dtpSendDateTimeConfirmation.SelectedDateTime.Value < RockDateTime.Now )
                {
                    nbSendDateTimeWarningConfirmation.NotificationBoxType = NotificationBoxType.Danger;
                    nbSendDateTimeWarningConfirmation.Text = "Send Date Time must be immediate or a future date/time";
                    nbSendDateTimeWarningConfirmation.Visible = true;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Updates the communication.
        /// </summary>
        /// <param name="rockContext">The rock context.</param>
        /// <returns></returns>
        private Rock.Model.Communication UpdateCommunication( RockContext rockContext )
        {
            var communicationService = new CommunicationService( rockContext );
            var communicationAttachmentService = new CommunicationAttachmentService( rockContext );
            var communicationRecipientService = new CommunicationRecipientService( rockContext );

            Rock.Model.Communication communication = null;
            IQueryable<CommunicationRecipient> qryRecipients = null;

            int communicationId = hfCommunicationId.Value.AsInteger();
            if ( communicationId > 0 )
            {
                communication = communicationService.Get( communicationId );
            }

            List<int> recipientPersonIds;
            if ( tglRecipientSelection.Checked )
            {
                recipientPersonIds = GetRecipientFromListSelection().Select( a => a.PersonId ).ToList();
            }
            else
            {
                recipientPersonIds = this.IndividualRecipientPersonIds;
            }

            if ( communication != null )
            {
                // Remove any deleted recipients
                HashSet<int> personIdHash = new HashSet<int>( recipientPersonIds );
                qryRecipients = communication.GetRecipientsQry( rockContext );

                foreach ( var item in qryRecipients.Select( a => new
                {
                    Id = a.Id,
                    PersonId = a.PersonAlias.PersonId
                } ) )
                {
                    if ( !personIdHash.Contains( item.PersonId ) )
                    {
                        var recipient = qryRecipients.Where( a => a.Id == item.Id ).FirstOrDefault();
                        communicationRecipientService.Delete( recipient );
                        communication.Recipients.Remove( recipient );
                    }
                }
            }

            if ( communication == null )
            {
                communication = new Rock.Model.Communication();
                communication.Status = CommunicationStatus.Transient;
                communication.SenderPersonAliasId = CurrentPersonAliasId;
                communicationService.Add( communication );
            }

            communication.EnabledLavaCommands = GetAttributeValue( "EnabledLavaCommands" );

            if ( qryRecipients == null )
            {
                qryRecipients = communication.GetRecipientsQry( rockContext );
            }

            // Add any new recipients
            HashSet<int> communicationPersonIdHash = new HashSet<int>( qryRecipients.Select( a => a.PersonAlias.PersonId ) );
            var recipientPersonIdsAliasId = new PersonService( rockContext ).Queryable().Where( a => recipientPersonIds.Contains( a.Id ) ).Select( a => new
            {
                PersonId = a.Id,
                PrimaryAliasId = a.Aliases.Where( x => x.AliasPersonId == x.PersonId ).Select( pa => pa.Id ).FirstOrDefault()
            } ).ToList();

            foreach ( var recipientPersonIdAliasId in recipientPersonIdsAliasId )
            {
                if ( !communicationPersonIdHash.Contains( recipientPersonIdAliasId.PersonId ) )
                {
                    var communicationRecipient = new CommunicationRecipient();
                    communicationRecipient.PersonAliasId = recipientPersonIdAliasId.PrimaryAliasId;
                    communication.Recipients.Add( communicationRecipient );
                }
            }

            communication.Name = tbCommunicationName.Text;
            communication.IsBulkCommunication = tglBulkCommunication.Checked;
            communication.CommunicationType = ( CommunicationType ) hfMediumType.Value.AsInteger();

            if ( tglRecipientSelection.Checked )
            {
                communication.ListGroupId = ddlCommunicationGroupList.SelectedValue.AsIntegerOrNull();
            }
            else
            {
                communication.ListGroup = null;
                communication.ListGroupId = null;
            }

            var emailMediumEntityType = EntityTypeCache.Read( Rock.SystemGuid.EntityType.COMMUNICATION_MEDIUM_EMAIL.AsGuid() );
            var smsMediumEntityType = EntityTypeCache.Read( Rock.SystemGuid.EntityType.COMMUNICATION_MEDIUM_SMS.AsGuid() );
            List<GroupMember> communicationListGroupMemberList = null;
            if ( communication.ListGroupId.HasValue )
            {
                communicationListGroupMemberList = new GroupMemberService( rockContext ).Queryable().Where( a => a.GroupId == communication.ListGroupId.Value && a.GroupMemberStatus == GroupMemberStatus.Active ).ToList();
            }

            foreach ( var recipient in communication.Recipients )
            {
                if ( communication.CommunicationType == CommunicationType.Email )
                {
                    recipient.MediumEntityTypeId = emailMediumEntityType.Id;
                }
                else if ( communication.CommunicationType == CommunicationType.SMS )
                {
                    recipient.MediumEntityTypeId = smsMediumEntityType.Id;
                }
                else if ( communication.CommunicationType == CommunicationType.RecipientPreference )
                {
                    // if emailing to a communication list, first see if the recipient has a CommunicationPreference set as a GroupMember attribute for this list
                    CommunicationType? recipientPreference = null;
                    if ( communicationListGroupMemberList != null )
                    {
                        var groupMember = communicationListGroupMemberList.FirstOrDefault( a => a.PersonId == recipient.PersonAlias.PersonId );
                        if ( groupMember != null )
                        {
                            groupMember.LoadAttributes( rockContext );

                            recipientPreference = ( CommunicationType? ) groupMember.GetAttributeValue( "PreferredCommunicationType" ).AsIntegerOrNull();
                        }
                    }

                    // if not emailing to a communication list, or the recipient doesn't have a preference set in the list, get the preference from the Person record
                    if ( recipientPreference == null || recipientPreference == CommunicationType.RecipientPreference )
                    {
                        recipientPreference = recipient.PersonAlias.Person.CommunicationPreference;
                    }

                    if ( recipientPreference == CommunicationType.SMS )
                    {
                        // if the Recipient's preferred communication type is SMS, use that as the medium for this recipient
                        recipient.MediumEntityTypeId = smsMediumEntityType.Id;
                    }
                    else if ( recipientPreference == CommunicationType.Email )
                    {
                        // if the Recipient's preferred communication type is Email, use that as the medium for this recipient
                        recipient.MediumEntityTypeId = emailMediumEntityType.Id;
                    }
                    else
                    {
                        // if the Recipient's preferred communication type neither Email or SMS, or not specified, default to sending as an Email
                        recipient.MediumEntityTypeId = emailMediumEntityType.Id;
                    }
                }
                else
                {
                    throw new Exception( "Unexpected CommunicationType: " + communication.CommunicationType.ConvertToString() );
                }
            }

            var segmentDataViewIds = cblCommunicationGroupSegments.Items.OfType<ListItem>().Where( a => a.Selected ).Select( a => a.Value.AsInteger() ).ToList();
            var segmentDataViewGuids = new DataViewService( rockContext ).GetByIds( segmentDataViewIds ).Select( a => a.Guid ).ToList();

            communication.Segments = segmentDataViewGuids.AsDelimited( "," );
            communication.SegmentCriteria = rblCommunicationGroupSegmentFilterType.SelectedValueAsEnum<SegmentCriteria>();

            communication.CommunicationTemplateId = hfSelectedCommunicationTemplateId.Value.AsIntegerOrNull();

            communication.FromName = tbFromName.Text;
            communication.FromEmail = ebFromAddress.Text;
            communication.ReplyToEmail = ebReplyToAddress.Text;
            communication.CCEmails = ebCCList.Text;
            communication.BCCEmails = ebBCCList.Text;

            List<int> emailBinaryFileIds = hfEmailAttachedBinaryFileIds.Value.SplitDelimitedValues().AsIntegerList();
            List<int> smsBinaryFileIds = new List<int>();

            if ( fupMobileAttachment.BinaryFileId.HasValue )
            {
                smsBinaryFileIds.Add( fupMobileAttachment.BinaryFileId.Value );
            }

            // delete any attachments that are no longer included
            foreach ( var attachment in communication.Attachments.Where( a => ( !emailBinaryFileIds.Contains( a.BinaryFileId ) && !smsBinaryFileIds.Contains( a.BinaryFileId ) ) ).ToList() )
            {
                communication.Attachments.Remove( attachment );
                communicationAttachmentService.Delete( attachment );
            }

            // add any new email attachments that were added
            foreach ( var attachmentBinaryFileId in emailBinaryFileIds.Where( a => !communication.Attachments.Any( x => x.BinaryFileId == a ) ) )
            {
                communication.Attachments.Add( new CommunicationAttachment { BinaryFileId = attachmentBinaryFileId, CommunicationType = CommunicationType.Email } );
            }

            // add any new SMS attachments that were added
            foreach ( var attachmentBinaryFileId in smsBinaryFileIds.Where( a => !communication.Attachments.Any( x => x.BinaryFileId == a ) ) )
            {
                communication.Attachments.Add( new CommunicationAttachment { BinaryFileId = attachmentBinaryFileId, CommunicationType = CommunicationType.SMS } );
            }

            communication.Subject = tbEmailSubject.Text;
            communication.Message = hfEmailEditorHtml.Value;

            communication.SMSFromDefinedValueId = ddlSMSFrom.SelectedValue.AsIntegerOrNull();
            communication.SMSMessage = tbSMSTextMessage.Text;

            if ( tglSendDateTimeConfirmation.Checked )
            {
                communication.FutureSendDateTime = null;
            }
            else
            {
                communication.FutureSendDateTime = dtpSendDateTimeConfirmation.SelectedDateTime;
            }

            return communication;
        }

        /// <summary>
        /// Handles the CheckedChanged event of the tglSendDateTimeConfirmation control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        protected void tglSendDateTimeConfirmation_CheckedChanged( object sender, EventArgs e )
        {
            nbSendDateTimeWarningConfirmation.Visible = false;
            dtpSendDateTimeConfirmation.Visible = !tglSendDateTimeConfirmation.Checked;
        }

        #endregion



        
    }
}